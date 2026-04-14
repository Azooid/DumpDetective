using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Enumerates System.Threading.Timer, System.Timers.Timer, and related types.
// Resolves callbacks to method names, buckets timers by period range, and alerts
// on high-count or high-frequency timer accumulation.
internal static class TimerLeaksCommand
{
    private const string Help = """
        Usage: DumpDetective timer-leaks <dump-file> [options]

        Options:
          -a, --addresses    Show individual timer object addresses (up to 200 per type)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    private static readonly HashSet<string> TimerTypeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Threading.TimerQueueTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Windows.Forms.Timer",
    };

    private sealed record TimerInfo(
        string Type, ulong Addr, long Size,
        string Callback, string Module,
        long DueMs, long PeriodMs);

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

        var timers = ScanTimers(ctx);

        sink.Section("Summary");
        if (timers.Count == 0) { sink.Text("No timer objects found."); return; }

        long totalSize = timers.Sum(t => t.Size);
        sink.KeyValues([
            ("Total timer objects", timers.Count.ToString("N0")),
            ("Total size",          DumpHelpers.FormatSize(totalSize)),
        ]);

        if (timers.Count > 500)
            sink.Alert(AlertLevel.Critical, $"{timers.Count:N0} timer objects on heap.",
                advice: "Dispose System.Timers.Timer when no longer needed. Prefer System.Threading.PeriodicTimer (auto-disposing).");
        else if (timers.Count > 100)
            sink.Alert(AlertLevel.Warning, $"{timers.Count:N0} timer objects detected.");

        RenderTypeGroups(sink, timers, showAddr);
        RenderPeriodDistribution(sink, timers);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Walks the heap for objects whose type is in TimerTypeSet, resolves the callback
    // delegate to a method name, and reads _dueTime / _period fields.
    static List<TimerInfo> ScanTimers(DumpContext ctx)
    {
        var timers = new List<TimerInfo>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning timer objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!TimerTypeSet.Contains(obj.Type.Name ?? string.Empty)) continue;
                var (cb, module) = ResolveCallback(obj, ctx.Runtime);
                timers.Add(new TimerInfo(
                    obj.Type.Name!, obj.Address, (long)obj.Size,
                    cb, module,
                    ReadTimerLong(obj, "_dueTime"),
                    ReadTimerLong(obj, "_period")));
            }
        });
        return timers;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Collapsible accordion per timer type showing callback breakdown.
    // Optionally adds a per-address table (capped at 200 rows per group).
    static void RenderTypeGroups(IRenderSink sink, List<TimerInfo> timers, bool showAddr)
    {
        foreach (var g in timers.GroupBy(t => t.Type).OrderByDescending(g => g.Count()))
        {
            long grpSize = g.Sum(t => t.Size);
            sink.BeginDetails($"{g.Key}  —  {g.Count():N0} instance(s)  |  {DumpHelpers.FormatSize(grpSize)}", open: g.Count() > 10);
            var cbGroups = g
                .GroupBy(t => t.Callback.Length > 0 ? t.Callback : "<unknown>")
                .OrderByDescending(cg => cg.Count())
                .Select(cg => new[]
                {
                    cg.Key,
                    cg.First().Module.Length > 0 ? cg.First().Module : "—",
                    cg.Count().ToString("N0"),
                    FormatInterval(cg.First().PeriodMs),
                    FormatInterval(cg.First().DueMs),
                })
                .ToList();
            sink.Table(["Callback Method", "Module (DLL)", "Count", "Period", "Due In"], cbGroups);

            if (showAddr)
            {
                var addrRows = g.Take(200).Select(t => new[]
                {
                    $"0x{t.Addr:X16}",
                    t.Callback.Length > 0 ? t.Callback : "—",
                    FormatInterval(t.PeriodMs),
                    FormatInterval(t.DueMs),
                    DumpHelpers.FormatSize(t.Size),
                }).ToList();
                sink.Table(["Address", "Callback", "Period", "Due In", "Size"], addrRows);
            }
            sink.EndDetails();
        }
    }

    // Period bucket frequency table + high-frequency timer alert.
    static void RenderPeriodDistribution(IRenderSink sink, List<TimerInfo> timers)
    {
        sink.Section("Timer Period Distribution");
        var periodBuckets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in timers)
        {
            string bucket = t.PeriodMs switch
            {
                -1          => "Infinite (one-shot)",
                0           => "0 ms (immediate)",
                < 100       => "< 100 ms (high-frequency)",
                < 1_000     => "100 ms – 1 s",
                < 10_000    => "1 s – 10 s",
                < 60_000    => "10 s – 1 min",
                < 3_600_000 => "1 min – 1 hr",
                _           => "> 1 hr",
            };
            periodBuckets[bucket] = periodBuckets.GetValueOrDefault(bucket, 0) + 1;
        }
        var periodRows = periodBuckets
            .OrderBy(kv => PeriodBucketSortKey(kv.Key))
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") })
            .ToList();
        sink.Table(["Period Range", "Count"], periodRows, "High-frequency timers cause CPU overhead");

        int highFreqCount = timers.Count(t => t.PeriodMs >= 0 && t.PeriodMs < 100);
        if (highFreqCount > 10)
            sink.Alert(AlertLevel.Warning,
                $"{highFreqCount} timer(s) firing more than 10×/sec (period < 100 ms).",
                "High-frequency timers create CPU and GC pressure.",
                "Consolidate short-interval timers into a single dispatcher or use System.Threading.PeriodicTimer.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Resolves the callback delegate on a timer object to a human-readable method name
    // by reading _methodPtr via ClrRuntime.GetMethodByInstructionPointer.
    static (string Callback, string Module) ResolveCallback(ClrObject obj, ClrRuntime runtime)
    {
        try
        {
            var cb = obj.ReadObjectField("m_callback");
            if (cb.IsNull || !cb.IsValid) return ("", "");
            ulong ptr = cb.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return (cb.Type?.Name ?? "", "");
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return (cb.Type?.Name ?? "", "");
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            string method   = $"{typePart}{m.Name}";
            string module   = Path.GetFileName(m.Type?.Module?.Name ?? "");
            return (method, module);
        }
        catch { return ("", ""); }
    }

    // Reads a due-time or period field as long first, then falls back to int.
    // Returns -1 (infinite/not-set sentinel) on field-not-found.
    static long ReadTimerLong(ClrObject obj, string fieldName)
    {
        try { return obj.ReadField<long>(fieldName); } catch { }
        try { return obj.ReadField<int>(fieldName); }  catch { }
        return -1;
    }

    // Formats a millisecond interval as ∞ / "0 ms" / seconds / minutes.
    static string FormatInterval(long ms) => ms switch
    {
        -1       => "∞",
        0        => "0 ms",
        < 1_000  => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:F1} s",
        _        => $"{ms / 60_000.0:F1} min",
    };

    // Numeric sort key for the period bucket strings so the table reads in ascending order.
    static int PeriodBucketSortKey(string b) => b switch
    {
        "Infinite (one-shot)"       => 0,
        "0 ms (immediate)"          => 1,
        "< 100 ms (high-frequency)" => 2,
        "100 ms – 1 s"              => 3,
        "1 s – 10 s"                => 4,
        "10 s – 1 min"              => 5,
        "1 min – 1 hr"              => 6,
        "> 1 hr"                    => 7,
        _                           => 8,
    };
}
