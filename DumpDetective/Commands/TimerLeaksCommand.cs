using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TimerLeaksCommand
{
    private const string Help = """
        Usage: DumpDetective timer-leaks <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses (up to 200)
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    private static readonly HashSet<string> TimerTypeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Threading.TimerQueueTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Windows.Forms.Timer",
    };

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Timer Leak Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var timers = new List<(string Type, ulong Addr, long Size, string Callback, long DueMs, long PeriodMs)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning timer objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!TimerTypeSet.Contains(obj.Type.Name ?? string.Empty)) continue;

                long size     = (long)obj.Size;
                string cb     = ResolveCallback(obj, ctx.Runtime);
                long dueMs    = ReadTimerLong(obj, "_dueTime");
                long periodMs = ReadTimerLong(obj, "_period");

                timers.Add((obj.Type.Name!, obj.Address, size, cb, dueMs, periodMs));
            }
        });

        sink.Section("Summary");
        if (timers.Count == 0) { sink.Text("No timer objects found."); return; }

        long totalSize = timers.Sum(t => t.Size);
        sink.KeyValues([
            ("Total timer objects",  timers.Count.ToString("N0")),
            ("Total size",           DumpHelpers.FormatSize(totalSize)),
        ]);

        if (timers.Count > 500)
            sink.Alert(AlertLevel.Critical, $"{timers.Count:N0} timer objects on heap.",
                advice: "Dispose System.Timers.Timer when no longer needed. Prefer System.Threading.PeriodicTimer (auto-disposing).");
        else if (timers.Count > 100)
            sink.Alert(AlertLevel.Warning, $"{timers.Count:N0} timer objects detected.");

        // Summary by type
        var grouped = timers
            .GroupBy(t => t.Type)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var g in grouped)
        {
            long grpSize = g.Sum(t => t.Size);
            sink.BeginDetails($"{g.Key}  —  {g.Count():N0} instance(s)  |  {DumpHelpers.FormatSize(grpSize)}", open: g.Count() > 10);

            // Callback breakdown
            var cbGroups = g
                .GroupBy(t => t.Callback.Length > 0 ? t.Callback : "<unknown>")
                .OrderByDescending(cg => cg.Count())
                .Select(cg => new[]
                {
                    cg.Key,
                    cg.Count().ToString("N0"),
                    FormatInterval(cg.First().PeriodMs),
                    FormatInterval(cg.First().DueMs),
                })
                .ToList();
            sink.Table(["Callback Method", "Count", "Period", "Due In"], cbGroups);

            if (showAddr)
            {
                var addrRows = g.Take(200).Select(t => new[]
                {
                    $"0x{t.Addr:X16}",
                    t.Callback.Length > 0 ? t.Callback : "<unknown>",
                    FormatInterval(t.PeriodMs),
                    FormatInterval(t.DueMs),
                    DumpHelpers.FormatSize(t.Size),
                }).ToList();
                sink.Table(["Address", "Callback", "Period", "Due In", "Size"], addrRows);
            }

            sink.EndDetails();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string ResolveCallback(ClrObject obj, ClrRuntime runtime)
    {
        // m_callback on TimerQueueTimer is a TimerCallback delegate
        try
        {
            var cb = obj.ReadObjectField("m_callback");
            if (cb.IsNull || !cb.IsValid) return "";
            ulong ptr = cb.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return cb.Type?.Name ?? "";
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return cb.Type?.Name ?? "";
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            return $"{typePart}{m.Name}";
        }
        catch { return ""; }
    }

    static long ReadTimerLong(ClrObject obj, string fieldName)
    {
        try { return obj.ReadField<long>(fieldName); }    catch { }
        try { return obj.ReadField<int>(fieldName); }     catch { }
        return -1;
    }

    static string FormatInterval(long ms) => ms switch
    {
        -1   => "∞",
        0    => "0 ms",
        < 1000 => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:F1} s",
        _ => $"{ms / 60_000.0:F1} min",
    };
}
