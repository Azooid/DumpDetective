using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class AsyncStacksCommand
{
    private const string Help = """
        Usage: DumpDetective async-stacks <dump-file> [options]

        Options:
          -f, --filter <t>   Only show state machines whose type contains <t>
          -n, --top <N>      Top N methods (default: 50)
          -a, --addresses    Show individual state machine addresses (up to 200)
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; string? filter = null; bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)         int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "--addresses" or "-a")                        showAddr = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, filter, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, string? filter = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Async State Machines",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // (method, stateLabel) → count
        var counts   = new Dictionary<(string Method, string State), int>(EqualityComparer<(string, string)>.Default);
        var addrList = new List<(string Method, string State, ulong Addr)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning async state machines...", statusCtx =>
        {
            var watch  = Stopwatch.StartNew();
            long scanned = 0;

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.Contains(">d__", StringComparison.Ordinal) &&
                    !name.Contains(">D__", StringComparison.Ordinal)) continue;

                var method = ExtractMethod(name);
                if (filter != null && !method.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                // Read <>1__state: -2=initial, -1=completed/faulted, >=0=suspended at await N
                string stateLabel = ReadStateLabel(obj);

                var key = (method, stateLabel);
                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;

                if (showAddr) addrList.Add((method, stateLabel, obj.Address));

                scanned++;
                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    statusCtx.Status($"Scanning async state machines — {scanned:N0} found...");
                    watch.Restart();
                }
            }
        });

        int total = counts.Values.Sum();

        sink.Section("Summary");
        sink.KeyValues([
            ("Total state machines",    total.ToString("N0")),
            ("Unique methods",          counts.Keys.Select(k => k.Method).Distinct().Count().ToString("N0")),
            ("Suspended (awaiting)",    counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value).ToString("N0")),
            ("Running",                 counts.Where(kv => kv.Key.State == "Running").Sum(kv => kv.Value).ToString("N0")),
            ("Completed / Faulted",     counts.Where(kv => kv.Key.State == "Completed").Sum(kv => kv.Value).ToString("N0")),
        ]);

        if (total == 0) { sink.Text("No suspended async state machines found."); return; }

        int suspendedTotal = counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value);
        if (suspendedTotal > 1000)
            sink.Alert(AlertLevel.Critical, $"{suspendedTotal:N0} async state machines suspended (awaiting).",
                advice: "Investigate task backlog — check thread-pool saturation with thread-pool command.");
        else if (suspendedTotal > 100)
            sink.Alert(AlertLevel.Warning, $"{suspendedTotal:N0} async state machines suspended.");

        // Group by method + state
        var rows = counts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new[]
            {
                kv.Key.Method,
                kv.Key.State,
                kv.Value.ToString("N0"),
                $"{kv.Value * 100.0 / Math.Max(1, total):F1}%",
                kv.Key.State == "Awaiting" ? ClassifyAwait(kv.Key.Method) : "",
            })
            .ToList();

        sink.Section($"Top {rows.Count} State Machines by Method + State");
        sink.Table(["Method", "State", "Count", "%", "Await Hint"], rows,
            $"Top {rows.Count} of {counts.Count} unique (method, state) combinations");

        // State breakdown sub-summary
        var stateBreakdown = counts
            .GroupBy(kv => kv.Key.State)
            .Select(g => new[] { g.Key, g.Sum(kv => kv.Value).ToString("N0"), $"{g.Sum(kv => kv.Value) * 100.0 / Math.Max(1, total):F1}%" })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .ToList();
        sink.Table(["State", "Count", "%"], stateBreakdown, "State distribution");

        if (showAddr && addrList.Count > 0)
        {
            sink.Section("Individual State Machine Addresses");
            var addrRows = addrList
                .Where(a => a.State == "Awaiting")   // prioritise suspended ones
                .Take(200)
                .Select(a => new[] { a.Method, a.State, $"0x{a.Addr:X16}" })
                .ToList();
            sink.Table(["Method", "State", "Address"], addrRows,
                $"Showing up to 200 suspended instances (use WinDbg !dumpobj <addr> to inspect)");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string ReadStateLabel(in Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        try
        {
            var stateField = obj.Type?.GetFieldByName("<>1__state");
            if (stateField is null) return "Unknown";
            int state = obj.ReadField<int>("<>1__state");
            return state switch
            {
                -2 => "Initial",
                -1 => "Completed",
                >= 0 => "Awaiting",
                _ => "Unknown",
            };
        }
        catch { return "Unknown"; }
    }

    static string ExtractMethod(string typeName)
    {
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", lt < 0 ? 0 : lt, StringComparison.Ordinal);
        if (lt < 0 || gt < 0) return typeName;
        return $"{typeName[..lt].TrimEnd('+')} .{typeName[(lt + 1)..gt]}";
    }

    static string ClassifyAwait(string method)
    {
        if (method.Contains("Http",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Request",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Rest",     StringComparison.OrdinalIgnoreCase) ||
            method.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase))
            return "HTTP/Network";
        if (method.Contains("Sql",      StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Query",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Database", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("DbContext",StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Execute",  StringComparison.OrdinalIgnoreCase) ||
            (method.Contains("Db",      StringComparison.OrdinalIgnoreCase) &&
             !method.Contains("Debug",  StringComparison.OrdinalIgnoreCase)))
            return "Database";
        if (method.Contains("Redis",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Cache",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Memcache", StringComparison.OrdinalIgnoreCase))
            return "Cache";
        if (method.Contains("Queue",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("ServiceBus",StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Kafka",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Message",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Publish",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Consume",  StringComparison.OrdinalIgnoreCase))
            return "Messaging";
        if (method.Contains("File",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Stream",   StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Read",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Write",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("IO",       StringComparison.OrdinalIgnoreCase))
            return "File/I/O";
        if (method.Contains("Semaphore",StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Mutex",    StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Lock",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("WaitAsync",StringComparison.OrdinalIgnoreCase))
            return "Lock/Sync";
        return "";
    }
}
