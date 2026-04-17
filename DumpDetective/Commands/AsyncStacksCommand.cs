using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

// Scans a heap dump for compiler-generated async state-machine objects (IAsyncStateMachine
// implementations) and reports their distribution by method name and suspension state.
internal static class AsyncStacksCommand
{
    private const string Help = """
        Usage: DumpDetective async-stacks <dump-file> [options]

        Options:
          -f, --filter <t>   Only show state machines whose type contains <t>
          -n, --top <N>      Top N methods (default: 50)
          -a, --addresses    Show individual state machine addresses (up to 200)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    // One heap-resident async state-machine instance discovered during the heap walk.
    private readonly record struct StateMachineEntry(string Method, string State, ulong Addr);

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50;
        string? filter = null;
        bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)
                int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length)
                filter = args[++i];
            else if (args[i] is "--addresses" or "-a")
                showAddr = true;
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

        var entries = ScanStateMachines(ctx, filter);
        var counts  = BuildCounts(entries);
        int total   = counts.Values.Sum();

        RenderSummary(sink, counts, total);

        if (total == 0) { sink.Text("No suspended async state machines found."); return; }

        RenderAlerts(sink, counts, total);
        RenderTopTable(sink, counts, total, top);
        RenderStateBreakdown(sink, counts, total);

        if (showAddr && entries.Count > 0)
            RenderAddresses(sink, entries);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Walks every heap object, identifies compiler-generated async state-machine types by their
    // name pattern (contains ">d__" or ">D__"), and records the method name and suspension state.
    static List<StateMachineEntry> ScanStateMachines(DumpContext ctx, string? filter)
    {
        var entries = new List<StateMachineEntry>();

        CommandBase.RunStatus("Scanning async state machines...", upd =>
        {
            var watch    = Stopwatch.StartNew();
            long scanned = 0;

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                // Compiler-generated state machine types embed the async method name
                // between angle-brackets followed by d__ or D__ (C# vs VB naming).
                if (!name.Contains(">d__", StringComparison.Ordinal) &&
                    !name.Contains(">D__", StringComparison.Ordinal)) continue;

                var method = ExtractMethod(name);
                if (filter != null && !method.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                // Read <>1__state: -2=initial, -1=completed/faulted, >=0=suspended at await N
                string stateLabel = ReadStateLabel(obj);
                entries.Add(new StateMachineEntry(method, stateLabel, obj.Address));

                scanned++;
                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    upd($"Scanning async state machines — {scanned:N0} found...");
                    watch.Restart();
                }
            }
        });

        return entries;
    }

    // Aggregates individual entries into a (Method, State) → count dictionary.
    static Dictionary<(string Method, string State), int> BuildCounts(List<StateMachineEntry> entries) =>
        entries
            .GroupBy(e => (e.Method, e.State))
            .ToDictionary(g => g.Key, g => g.Count());

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Top-level key-value summary: totals broken down by lifecycle state.
    static void RenderSummary(IRenderSink sink, Dictionary<(string Method, string State), int> counts, int total)
    {
        sink.Section("Summary");
        sink.KeyValues([
            ("Total state machines",    total.ToString("N0")),
            ("Unique methods",          counts.Keys.Select(k => k.Method).Distinct().Count().ToString("N0")),
            ("Suspended (awaiting)",    counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value).ToString("N0")),
            ("Running",                 counts.Where(kv => kv.Key.State == "Running").Sum(kv => kv.Value).ToString("N0")),
            ("Completed / Faulted",     counts.Where(kv => kv.Key.State == "Completed").Sum(kv => kv.Value).ToString("N0")),
        ]);
    }

    // Emits a critical or warning alert when suspended counts exceed actionable thresholds.
    static void RenderAlerts(IRenderSink sink, Dictionary<(string Method, string State), int> counts, int total)
    {
        int suspendedTotal = counts.Where(kv => kv.Key.State == "Awaiting").Sum(kv => kv.Value);
        if (suspendedTotal > 1000)
            sink.Alert(AlertLevel.Critical, $"{suspendedTotal:N0} async state machines suspended (awaiting).",
                advice: "Investigate task backlog — check thread-pool saturation with thread-pool command.");
        else if (suspendedTotal > 100)
            sink.Alert(AlertLevel.Warning, $"{suspendedTotal:N0} async state machines suspended.");
    }

    // Top-N table: one row per unique (method, state) pair, sorted by count descending,
    // with a heuristic I/O-category hint for suspended instances.
    static void RenderTopTable(IRenderSink sink, Dictionary<(string Method, string State), int> counts, int total, int top)
    {
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
    }

    // State-bucket breakdown: percentage share of each lifecycle state across all instances.
    static void RenderStateBreakdown(IRenderSink sink, Dictionary<(string Method, string State), int> counts, int total)
    {
        var stateBreakdown = counts
            .GroupBy(kv => kv.Key.State)
            .Select(g => new[] { g.Key, g.Sum(kv => kv.Value).ToString("N0"), $"{g.Sum(kv => kv.Value) * 100.0 / Math.Max(1, total):F1}%" })
            .OrderByDescending(r => int.Parse(r[1].Replace(",", "")))
            .ToList();
        sink.Table(["State", "Count", "%"], stateBreakdown, "State distribution");
    }

    // Section (--addresses): raw object addresses for use with WinDbg !dumpobj / !gcroot.
    // Shows up to 200 suspended instances, prioritised over completed/initial ones.
    static void RenderAddresses(IRenderSink sink, List<StateMachineEntry> entries)
    {
        sink.Section("Individual State Machine Addresses");
        var addrRows = entries
            .Where(a => a.State == "Awaiting")   // prioritise suspended ones
            .Take(200)
            .Select(a => new[] { a.Method, a.State, $"0x{a.Addr:X16}" })
            .ToList();
        sink.Table(["Method", "State", "Address"], addrRows,
            $"Showing up to 200 suspended instances (use WinDbg !dumpobj <addr> to inspect)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Reads the <>1__state field to determine the state machine's lifecycle position.
    // -2 = not yet started (Initial), -1 = ran to completion or faulted, >=0 = suspended at await N.
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

    // Extracts the human-readable async method name from a compiler-generated state-machine type name.
    // e.g. "MyApp.Service+<FetchDataAsync>d__5"  →  "MyApp.Service .FetchDataAsync"
    static string ExtractMethod(string typeName)
    {
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", lt < 0 ? 0 : lt, StringComparison.Ordinal);
        if (lt < 0 || gt < 0) return typeName;
        return $"{typeName[..lt].TrimEnd('+')} .{typeName[(lt + 1)..gt]}";
    }

    // Heuristically classifies the likely I/O category of an awaited operation from the method name.
    // Used to populate the "Await Hint" column in the top-N table without inspecting the IL.
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
