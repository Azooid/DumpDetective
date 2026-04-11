using DumpDetective.Collectors;
using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Models;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TrendAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective trend-analysis <dump1> <dump2> [<dump3> ...] [options]
               DumpDetective trend-analysis <dump-directory> [options]
               DumpDetective trend-analysis --list <paths.txt> [options]

        Analyzes multiple dumps and reports memory/leak trends over time.
        Dumps are sorted by file modification time to establish the timeline.

        Options:
          --list <file>          Read dump paths from a text file (one path per line)
                                 Entries can be dump files or directories.
          --full                 Run full collection per dump (includes event leaks,
                                 string duplicates — slower but more data)
          --ignore-event <type>  Exclude publisher types whose name contains <type>
                                 from the Event Leak Analysis table. Repeatable.
                                 Example: --ignore-event SNINativeMethodWrapper
          -o, --output <f>       Write report to file (.md / .html / .txt)
          -h, --help             Show this help

        Example:
          DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
          DumpDetective trend-analysis D:\\dumps --output trends.html
          DumpDetective trend-analysis --list dumps.txt --full --output report.md
          DumpDetective trend-analysis d1.dmp d2.dmp --full \\
              --ignore-event SNINativeMethodWrapper --ignore-event System.Data
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var    inputs      = new List<string>();
        bool   full        = false;
        string? output     = null;
        string? listFile   = null;
        var ignoreEvents   = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length)
                output = args[++i];
            else if (args[i] == "--full")
                full = true;
            else if (args[i] == "--list" && i + 1 < args.Length)
                listFile = args[++i];
            else if (args[i] == "--ignore-event" && i + 1 < args.Length)
                ignoreEvents.Add(args[++i]);
            else if (!args[i].StartsWith('-'))
                inputs.Add(args[i]);
        }

        // Load from list file
        if (listFile is not null)
        {
            if (!File.Exists(listFile))
            {
                Console.Error.WriteLine($"Error: list file not found: {listFile}");
                return 1;
            }
            inputs.AddRange(
                File.ReadAllLines(listFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var dumpPaths = ExpandDumpInputs(inputs, out var missingPaths, out var invalidDumpFiles);

        if (missingPaths.Count > 0)
        {
            foreach (var p in missingPaths)
                Console.Error.WriteLine($"Error: file or directory not found: {p}");
            return 1;
        }

        if (invalidDumpFiles.Count > 0)
        {
            foreach (var p in invalidDumpFiles)
                Console.Error.WriteLine($"Error: not a dump file (.dmp/.mdmp): {p}");
            return 1;
        }

        if (dumpPaths.Count < 2)
        {
            Console.Error.WriteLine("Error: at least 2 dump files are required for trend analysis (after directory expansion).");
            Console.Error.WriteLine(Help);
            return 1;
        }

        // Sort by file modification time to establish timeline
        dumpPaths = [.. dumpPaths.OrderBy(File.GetLastWriteTime)];

        AnsiConsole.MarkupLine($"[bold]Trend analysis:[/] {dumpPaths.Count} dump(s)  [[{(full ? "full" : "lightweight")} mode]]");
        AnsiConsole.WriteLine();

        var snapshots = new List<DumpDetective.Models.DumpSnapshot>();

        for (int i = 0; i < dumpPaths.Count; i++)
        {
            var label    = $"D{i + 1}";
            var path     = dumpPaths[i];
            var dispName = ShortName(path);
            DumpSnapshot? snap = null;

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .Start($"[bold]{label}[/]  {Markup.Escape(dispName)}  opening...", ctx =>
                {
                    void Upd(string msg) =>
                        ctx.Status($"[bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  {Markup.Escape(msg)}");
                    snap = full
                        ? DumpCollector.CollectFull(path, Upd)
                        : DumpCollector.CollectLightweight(path, Upd);
                });

            snapshots.Add(snap!);
            var sc = snap!.HealthScore >= 70 ? "green" : snap.HealthScore >= 40 ? "yellow" : "red";
            AnsiConsole.MarkupLine(
                $"  [green]✓[/]  [bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  " +
                $"[{sc}]{snap.HealthScore}/100  {ScoreLabel(snap.HealthScore)}[/]");

            // Non-blocking sweep between dumps — releases typeStats, stringValues,
            // delFieldsCache etc. while the next dump file is being opened (I/O time).
            // No compaction: avoids the multi-second STW pause from moving objects.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        AnsiConsole.WriteLine();

        using var sink = IRenderSink.Create(output);
        RenderTrend(snapshots, sink, ignoreEvents);

        // ── Full mode: append per-dump scored summary + embedded sub-reports ──
        if (full)
        {
            AnsiConsole.MarkupLine("[bold]Rendering per-dump detailed reports…[/]");
            for (int i = 0; i < dumpPaths.Count; i++)
            {
                var label    = $"D{i + 1}";
                var path     = dumpPaths[i];
                var snap     = snapshots[i];
                var dispName = ShortName(path);
                var sc       = snap.HealthScore >= 70 ? "green" : snap.HealthScore >= 40 ? "yellow" : "red";
                AnsiConsole.MarkupLine(
                    $"  [bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  " +
                    $"[{sc}]{snap.HealthScore}/100  {ScoreLabel(snap.HealthScore)}[/]");

                // Chapter header for this dump
                sink.Header(
                    $"Per-Dump Report: {label}  —  {Path.GetFileName(path)}",
                    $"{snap.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {snap.ClrVersion ?? "unknown"}  |  Score: {snap.HealthScore}/100  {ScoreLabel(snap.HealthScore)}");

                // Mini scored summary (uses already-collected snapshot, no extra I/O).
                // includeHeader:false avoids a duplicate "Analysis Report" hero —
                // the "Per-Dump Report: DX" hero written above is sufficient.
                AnalyzeCommand.RenderReport(snap, sink, includeHeader: false);

                // Full sub-reports — re-open dump for live heap-walk commands
                try
                {
                    using var ctx = DumpContext.Open(path);
                    AnalyzeCommand.RenderEmbeddedReports(ctx, sink);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠ Could not render sub-reports: {Markup.Escape(ex.Message)}[/]");
                    sink.Alert(AlertLevel.Warning,
                        $"Could not re-open {dispName} for sub-reports: {ex.Message}");
                }

                // Release memory between dumps
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }

        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    private static List<string> ExpandDumpInputs(
        IEnumerable<string> inputs,
        out List<string> missingPaths,
        out List<string> invalidDumpFiles)
    {
        var dumps = new List<string>();
        missingPaths = [];
        invalidDumpFiles = [];

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                dumps.AddRange(Directory.EnumerateFiles(input, "*.dmp", SearchOption.AllDirectories));
                dumps.AddRange(Directory.EnumerateFiles(input, "*.mdmp", SearchOption.AllDirectories));
                continue;
            }

            if (File.Exists(input))
            {
                if (IsDumpFile(input))
                    dumps.Add(input);
                else
                    invalidDumpFiles.Add(input);
                continue;
            }

            missingPaths.Add(input);
        }

        return [.. dumps.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsDumpFile(string path) =>
        path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase);

    // ── Renderer ──────────────────────────────────────────────────────────────

    internal static void RenderTrend(List<DumpSnapshot> snaps, IRenderSink sink,
                                     IReadOnlyList<string>? ignoreEventTypes = null)
    {
        var s0    = snaps[0];
        var sN    = snaps[^1];
        bool full = snaps.Any(s => s.IsFullMode);

        // Assign D1 … Dn labels used throughout the report
        var labels = snaps.Select((_, i) => $"D{i + 1}").ToArray();

        sink.Header(
            "Dump Detective — Trend Analysis Report",
            $"{snaps.Count} dumps  |  {s0.FileTime:yyyy-MM-dd HH:mm} → {sN.FileTime:yyyy-MM-dd HH:mm}  |  {(full ? "Full" : "Lightweight")} mode");

        // ── 1. Dump Timeline ──────────────────────────────────────────────────
        sink.Section("0. Dump Timeline");
        sink.Table(
            ["Dump", "File", "File Size", "Time", "Threads (Total / Alive)", "Health"],
            snaps.Select((s, i) => new[]
            {
                labels[i],
                Path.GetFileName(s.DumpPath),
                DumpHelpers.FormatSize(s.DumpFileSizeBytes),
                s.FileTime.ToString("HH:mm"),
                $"{s.ThreadCount} / {s.AliveThreadCount}",
                $"{s.HealthScore}/100  {ScoreLabel(s.HealthScore)}",
            }).ToList());

        // ── 1. Incident Summary ────────────────────────────────────────────────────
        sink.Section("1. Incident Summary");
        {
            // Build one row per signal; cell value shows each dump's reading + status emoji
            // Status logic: ✓ good  ⚠ warn  ✗ critical  — n/a
            static string Status(double val, double warnAt, double critAt, bool higherIsBad = true)
            {
                if (higherIsBad)
                    return val >= critAt ? "✗" : val >= warnAt ? "⚠" : "✓";
                else
                    return val <= critAt ? "✗" : val <= warnAt ? "⚠" : "✓";
            }

            string Cell(DumpSnapshot s, double val, string display, double warnAt, double critAt, bool higherIsBad = true)
                => $"{Status(val, warnAt, critAt, higherIsBad)} {display}";

            var incidentCols = new[] { "Signal" }.Concat(labels).Append("Trend").ToArray();
            var incidentRows = new List<string[]>();

            void IR(string name, Func<DumpSnapshot, (double val, string display)> proj,
                    double warnAt, double critAt, bool higherIsBad = true)
            {
                var cells = snaps.Select(s => { var (v, d) = proj(s); return Cell(s, v, d, warnAt, critAt, higherIsBad); }).ToArray();
                var (v0, _) = proj(s0); var (vN, _) = proj(sN);
                incidentRows.Add([name, .. cells, Trend(v0, vN, higherIsBad)]);
            }

            var tt = DumpDetective.Core.ThresholdLoader.Current.Trend;

            // Health
            IR("Health Score",
                s => (s.HealthScore, $"{s.HealthScore}/100 {ScoreLabel(s.HealthScore)}"),
                warnAt: tt.ScoreWarn, critAt: tt.ScoreCrit, higherIsBad: false);

            // Memory
            IR("Heap Total",
                s => (s.TotalHeapBytes / 1048576.0, DumpHelpers.FormatSize(s.TotalHeapBytes)),
                warnAt: tt.HeapWarnMb, critAt: tt.HeapCritMb);
            IR("LOH Size",
                s => (s.LohBytes / 1048576.0, DumpHelpers.FormatSize(s.LohBytes)),
                warnAt: tt.LohWarnMb, critAt: tt.LohCritMb);
            IR("Fragmentation",
                s => (s.HeapFreeBytes / 1048576.0, $"{s.FragmentationPct:F1}% ({DumpHelpers.FormatSize(s.HeapFreeBytes)})"),
                warnAt: tt.FragWarnMb, critAt: tt.FragCritMb);

            // Threads
            IR("Blocked Threads",
                s => (s.BlockedThreadCount, s.BlockedThreadCount.ToString("N0")),
                warnAt: tt.BlockedWarn, critAt: tt.BlockedCrit);
            IR("Async Backlog",
                s => (s.AsyncBacklogTotal, s.AsyncBacklogTotal.ToString("N0")),
                warnAt: tt.AsyncWarn, critAt: tt.AsyncCrit);

            // Exceptions
            IR("Active Exceptions",
                s => (s.ExceptionThreadCount, s.ExceptionThreadCount.ToString("N0")),
                warnAt: tt.ExceptionWarn, critAt: tt.ExceptionCrit);

            // Finalizer
            IR("Finalizer Queue",
                s => (s.FinalizerQueueDepth, s.FinalizerQueueDepth.ToString("N0")),
                warnAt: tt.FinalizerWarn, critAt: tt.FinalizerCrit);

            // Timers
            IR("Timer Objects",
                s => (s.TimerCount, s.TimerCount.ToString("N0")),
                warnAt: tt.TimerWarn, critAt: tt.TimerCrit);

            // WCF
            if (snaps.Any(s => s.WcfObjectCount > 0))
                IR("WCF Faulted Channels",
                    s => (s.WcfFaultedCount, s.WcfFaultedCount == 0 ? "—" : s.WcfFaultedCount.ToString("N0")),
                    warnAt: tt.WcfWarn, critAt: tt.WcfCrit);

            // Connections
            if (snaps.Any(s => s.ConnectionCount > 0))
                IR("DB Connections",
                    s => (s.ConnectionCount, s.ConnectionCount.ToString("N0")),
                    warnAt: tt.DbWarn, critAt: tt.DbCrit);

            // Handles — pinned handle growth is a GC pressure signal
            IR("Pinned Handles",
                s => (s.PinnedHandleCount, s.PinnedHandleCount.ToString("N0")),
                warnAt: tt.PinnedWarn, critAt: tt.PinnedCrit);

            // Event leaks
            IR("Event Subscribers",
                s => (s.EventSubscriberTotal, s.EventSubscriberTotal.ToString("N0")),
                warnAt: tt.EventWarn, critAt: tt.EventCrit);

            // String duplication (full mode only)
            if (snaps.Any(s => s.StringWastedBytes > 0))
                IR("String Waste",
                    s => (s.StringWastedBytes / 1048576.0, DumpHelpers.FormatSize(s.StringWastedBytes)),
                    warnAt: tt.StringWasteWarnMb, critAt: tt.StringWasteCritMb);

            sink.Table(incidentCols, incidentRows,
                "✓ good  ⚠ warning  ✗ critical  (thresholds are heuristic)");

            // ── Executive summary paragraph ───────────────────────────────────────
            var span      = sN.FileTime - s0.FileTime;
            var spanStr   = span.TotalHours >= 1
                ? $"{span.TotalHours:F1} hours" : $"{span.TotalMinutes:F0} minutes";
            var scoreChange = sN.HealthScore - s0.HealthScore;
            var scoreDir  = scoreChange > 0 ? $"improved by {scoreChange} points"
                : scoreChange < 0 ? $"declined by {Math.Abs(scoreChange)} points"
                : "remained stable";

            var criticals = new List<string>();
            var warnings  = new List<string>();

            void CheckSignal(string name, double val, double warnAt, double critAt, bool higherIsBad = true)
            {
                var st = Status(val, warnAt, critAt, higherIsBad);
                if      (st == "✗") criticals.Add(name);
                else if (st == "⚠") warnings.Add(name);
            }

            CheckSignal("Health Score",      sN.HealthScore,                      tt.ScoreWarn,         tt.ScoreCrit,         higherIsBad: false);
            CheckSignal("Heap Total",        sN.TotalHeapBytes / 1048576.0,       tt.HeapWarnMb,        tt.HeapCritMb);
            CheckSignal("LOH Size",          sN.LohBytes / 1048576.0,             tt.LohWarnMb,         tt.LohCritMb);
            CheckSignal("Fragmentation",     sN.HeapFreeBytes / 1048576.0,        tt.FragWarnMb,        tt.FragCritMb);
            CheckSignal("Blocked Threads",   sN.BlockedThreadCount,               tt.BlockedWarn,       tt.BlockedCrit);
            CheckSignal("Async Backlog",     sN.AsyncBacklogTotal,                tt.AsyncWarn,         tt.AsyncCrit);
            CheckSignal("Active Exceptions", sN.ExceptionThreadCount,             tt.ExceptionWarn,     tt.ExceptionCrit);
            CheckSignal("Finalizer Queue",   sN.FinalizerQueueDepth,              tt.FinalizerWarn,     tt.FinalizerCrit);
            CheckSignal("Timer Objects",     sN.TimerCount,                       tt.TimerWarn,         tt.TimerCrit);
            if (snaps.Any(s => s.WcfObjectCount > 0))
                CheckSignal("WCF Faulted Channels", sN.WcfFaultedCount,           tt.WcfWarn,           tt.WcfCrit);
            if (snaps.Any(s => s.ConnectionCount > 0))
                CheckSignal("DB Connections",       sN.ConnectionCount,           tt.DbWarn,            tt.DbCrit);
            CheckSignal("Pinned Handles",    sN.PinnedHandleCount,                tt.PinnedWarn,        tt.PinnedCrit);
            CheckSignal("Event Subscribers", sN.EventSubscriberTotal,             tt.EventWarn,         tt.EventCrit);
            if (snaps.Any(s => s.StringWastedBytes > 0))
                CheckSignal("String Waste",  sN.StringWastedBytes / 1048576.0,    tt.StringWasteWarnMb, tt.StringWasteCritMb);

            var sb = new System.Text.StringBuilder();
            sb.Append($"Analysis covers {snaps.Count} memory dump{(snaps.Count == 1 ? "" : "s")} " +
                      $"spanning {spanStr} ({s0.FileTime:yyyy-MM-dd HH:mm} → {sN.FileTime:yyyy-MM-dd HH:mm}). ");
            sb.Append($"The application health score {scoreDir} " +
                      $"({s0.HealthScore}/100 {ScoreLabel(s0.HealthScore)} → {sN.HealthScore}/100 {ScoreLabel(sN.HealthScore)}). ");
            if (criticals.Count == 0 && warnings.Count == 0)
            {
                sb.Append("All monitored signals are within acceptable thresholds. No immediate action is required.");
            }
            else
            {
                if (criticals.Count > 0)
                    sb.Append($"Critical signals requiring immediate attention: {string.Join(", ", criticals)}. ");
                if (warnings.Count > 0)
                    sb.Append($"Signals to monitor: {string.Join(", ", warnings)}. ");
                sb.Append(criticals.Count > 0
                    ? "Immediate investigation and remediation is recommended."
                    : "The system is operational but should be monitored to prevent escalation.");
            }

            // ── Findings across dumps ─────────────────────────────────────────
            // Collect all unique (Category, Headline, Severity) tuples across every snapshot
            // then show which dumps raised each finding, sorted Critical → Warning → Info.
            var allFindings = snaps
                .SelectMany(s => s.Findings.Select(f => (f.Severity, f.Category, f.Headline)))
                .Distinct()
                .OrderBy(f => f.Severity == FindingSeverity.Critical ? 0 : f.Severity == FindingSeverity.Warning ? 1 : 2)
                .ThenBy(f => f.Category)
                .ThenBy(f => f.Headline)
                .ToList();

            if (allFindings.Count > 0)
            {
                // One collapsible accordion per dump; inside, findings are rendered as
                // styled alert cards grouped Critical → Warning → Info.
                // First dump is open by default; subsequent ones are collapsed.
                int dumpIdx = 0;
                foreach (var (s, lbl) in snaps.Zip(labels))
                {
                    if (s.Findings.Count == 0) { dumpIdx++; continue; }
                    var critCount = s.Findings.Count(f => f.Severity == FindingSeverity.Critical);
                    var warnCount = s.Findings.Count(f => f.Severity == FindingSeverity.Warning);
                    var infoCount = s.Findings.Count - critCount - warnCount;
                    var badge     = $"{(critCount > 0 ? $"  ✗ {critCount} critical" : "")}" +
                                    $"{(warnCount > 0 ? $"  ⚠ {warnCount} warning"  : "")}" +
                                    $"{(infoCount  > 0 ? $"  ℹ {infoCount} info"     : "")}";
                    sink.BeginDetails($"{lbl}  —  Score {s.HealthScore}/100 {ScoreLabel(s.HealthScore)}{badge}", open: dumpIdx == 0);

                    foreach (var f in s.Findings.OrderBy(f => f.Severity == FindingSeverity.Critical ? 0 : f.Severity == FindingSeverity.Warning ? 1 : 2).ThenBy(f => f.Category))
                    {
                        var lvl = f.Severity == FindingSeverity.Critical ? AlertLevel.Critical
                                : f.Severity == FindingSeverity.Warning  ? AlertLevel.Warning
                                :                                          AlertLevel.Info;
                        sink.Alert(lvl, $"[{f.Category}] {f.Headline}", f.Detail, f.Advice);
                    }

                    sink.EndDetails();
                    dumpIdx++;
                }

                // Enrich executive summary with finding counts and any new/resolved findings
                var newFindings = sN.Findings
                    .Where(f => !s0.Findings.Any(x => x.Category == f.Category && x.Headline == f.Headline))
                    .ToList();
                var resolvedFindings = s0.Findings
                    .Where(f => !sN.Findings.Any(x => x.Category == f.Category && x.Headline == f.Headline))
                    .ToList();

                if (newFindings.Count > 0 || resolvedFindings.Count > 0)
                {
                    sb.Append(" Finding changes between first and last dump:");
                    if (newFindings.Count > 0)
                        sb.Append($" {newFindings.Count} new finding{(newFindings.Count == 1 ? "" : "s")} appeared" +
                                  $" ({string.Join("; ", newFindings.Take(3).Select(f => f.Headline))}{(newFindings.Count > 3 ? "…" : "")}).");
                    if (resolvedFindings.Count > 0)
                        sb.Append($" {resolvedFindings.Count} finding{(resolvedFindings.Count == 1 ? "" : "s")} resolved" +
                                  $" ({string.Join("; ", resolvedFindings.Take(3).Select(f => f.Headline))}{(resolvedFindings.Count > 3 ? "…" : "")}).");
                }
            }

            sink.Text(sb.ToString());
        }

        // ── 2. Overall Growth Summary ──────────────────────────────────────────────────
        sink.Section("2. Overall Growth Summary");
        var growthCols = new[] { "Metric" }.Concat(labels).Append("Trend").ToArray();
        var growthRows = new List<string[]>();

        void AddRow(string label, Func<DumpSnapshot, double> sel,
                    Func<DumpSnapshot, string> fmt, bool higherIsBad = true)
        {
            var vals = snaps.Select(s => fmt(s)).ToArray();
            growthRows.Add([label, .. vals, Trend(sel(s0), sel(sN), higherIsBad)]);
        }

        long SohBytes(DumpSnapshot s) =>
            s.TotalHeapBytes - s.LohBytes - s.PohBytes - s.FrozenBytes;

        AddRow("Total Objects",      s => s.TotalObjectCount,  s => s.TotalObjectCount.ToString("N0"));
        AddRow("Heap — SOH",         s => SohBytes(s),         s => DumpHelpers.FormatSize(SohBytes(s)));
        AddRow("Heap — LOH",         s => s.LohBytes,          s => DumpHelpers.FormatSize(s.LohBytes));
        AddRow("  LOH Live",          s => s.LohLiveBytes,       s => DumpHelpers.FormatSize(s.LohLiveBytes));
        AddRow("  LOH Free",          s => s.LohFreeBytes,       s => DumpHelpers.FormatSize(s.LohFreeBytes));
        AddRow("  LOH Frag %",        s => s.LohFragmentationPct,s => $"{s.LohFragmentationPct:F1}%");
        AddRow("Heap — Total",       s => s.TotalHeapBytes,    s => DumpHelpers.FormatSize(s.TotalHeapBytes));
        AddRow("Fragmentation",
            s => s.HeapFreeBytes,
            s => $"{s.FragmentationPct:F1}%  ({DumpHelpers.FormatSize(s.HeapFreeBytes)} free)");
        AddRow("LOH Object Count",    s => s.LohObjectCount,    s => s.LohObjectCount.ToString("N0"));
        AddRow("Finalize Queue",      s => s.FinalizerQueueDepth, s => s.FinalizerQueueDepth.ToString("N0"));
        AddRow("Unique Strings",      s => s.UniqueStringCount, s => s.UniqueStringCount.ToString("N0"));
        AddRow("Total String Mem",    s => s.StringTotalBytes,  s => DumpHelpers.FormatSize(s.StringTotalBytes));
        AddRow("Event Instances",     s => s.EventSubscriberTotal, s => s.EventSubscriberTotal.ToString("N0"));
        AddRow("Event Types",         s => s.EventLeakFieldCount,  s => s.EventLeakFieldCount.ToString("N0"));
        AddRow("Handles — Pinned",   s => s.PinnedHandleCount, s => s.PinnedHandleCount.ToString("N0"));
        AddRow("Handles — Strong",   s => s.StrongHandleCount, s => s.StrongHandleCount.ToString("N0"));
        AddRow("Handles — Weak",     s => s.WeakHandleCount,   s => s.WeakHandleCount.ToString("N0"),   higherIsBad: false);
        AddRow("Modules (App)",       s => s.AppModuleCount,    s => $"{s.AppModuleCount} / {s.ModuleCount}");
        sink.Table(growthCols, growthRows);

        // ── 3. Thread & Application Pressure ───────────────────────────────────
        sink.Section("3. Thread & Application Pressure");
        {
            // Helper builds a metric row: [label, d1val, d2val, ..., trend]
            string[] MRow(string label, Func<DumpSnapshot, string> fmt, string trend)
                => new[] { label }.Concat(snaps.Select(fmt)).Append(trend).ToArray();

            // Thread counts
            sink.Table(
                new[] { "Metric" }.Concat(labels).Append("Trend").ToArray(),
                new List<string[]>
                {
                    MRow("Threads (Total)",         s => s.ThreadCount.ToString("N0"),         Trend(s0.ThreadCount, sN.ThreadCount)),
                    MRow("  Alive",                 s => s.AliveThreadCount.ToString("N0"),    Trend(s0.AliveThreadCount, sN.AliveThreadCount)),
                    MRow("  Blocked",               s => s.BlockedThreadCount.ToString("N0"),  Trend(s0.BlockedThreadCount, sN.BlockedThreadCount)),
                    MRow("  With Active Exception", s => s.ExceptionThreadCount.ToString("N0"),Trend(s0.ExceptionThreadCount, sN.ExceptionThreadCount)),
                    MRow("Thread Pool — Active",    s => s.TpActiveWorkers.ToString("N0"),     Trend(s0.TpActiveWorkers, sN.TpActiveWorkers)),
                    MRow("Thread Pool — Idle",      s => s.TpIdleWorkers.ToString("N0"),       Trend(s0.TpIdleWorkers, sN.TpIdleWorkers, higherIsBad: false)),
                    MRow("Async Backlog",           s => s.AsyncBacklogTotal.ToString("N0"),   Trend(s0.AsyncBacklogTotal, sN.AsyncBacklogTotal)),
                    MRow("Timer Objects",           s => s.TimerCount.ToString("N0"),           Trend(s0.TimerCount, sN.TimerCount)),
                    MRow("WCF Objects",             s => s.WcfObjectCount.ToString("N0"),       Trend(s0.WcfObjectCount, sN.WcfObjectCount)),
                    MRow("  WCF Faulted",           s => s.WcfFaultedCount.ToString("N0"),     Trend(s0.WcfFaultedCount, sN.WcfFaultedCount)),
                    MRow("DB Connections",          s => s.ConnectionCount.ToString("N0"),      Trend(s0.ConnectionCount, sN.ConnectionCount)),
                },
                "Thread and application-level object counts across dumps");

            // GC generation breakdown
            sink.Table(
                new[] { "Generation" }.Concat(labels).Append("Trend").ToArray(),
                new List<string[]>
                {
                    MRow("Gen 0",          s => DumpHelpers.FormatSize(s.Gen0Bytes),       Trend(s0.Gen0Bytes, sN.Gen0Bytes)),
                    MRow("Gen 1",          s => DumpHelpers.FormatSize(s.Gen1Bytes),       Trend(s0.Gen1Bytes, sN.Gen1Bytes)),
                    MRow("Gen 2",          s => DumpHelpers.FormatSize(s.Gen2Bytes),       Trend(s0.Gen2Bytes, sN.Gen2Bytes)),
                    MRow("LOH",            s => DumpHelpers.FormatSize(s.LohBytes),        Trend(s0.LohBytes, sN.LohBytes)),
                    MRow("POH",            s => DumpHelpers.FormatSize(s.PohBytes),        Trend(s0.PohBytes, sN.PohBytes)),
                    MRow("Fragmentation (free)", s => $"{s.FragmentationPct:F1}%  ({DumpHelpers.FormatSize(s.HeapFreeBytes)} free)", Trend(s0.HeapFreeBytes, sN.HeapFreeBytes)),
                },
                "GC generation sizes and fragmentation across dumps");

            // Top exception types cross-dump
            var allExTypes = snaps
                .SelectMany(s => s.ExceptionCounts.Select(e => e.Type))
                .Distinct()
                .ToList();
            if (allExTypes.Count > 0)
            {
                var exCols = new[] { "Exception Type" }.Concat(labels).ToArray();
                var exRows = allExTypes
                    .Select(t =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var e = s.ExceptionCounts.FirstOrDefault(x => x.Type == t);
                            return e == default ? "—" : e.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[t, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",",""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(10)
                    .ToList();
                sink.Table(exCols, exRows, "Top exception types across dumps");
            }

            // Top async methods cross-dump
            var allAsyncMethods = snaps
                .SelectMany(s => s.TopAsyncMethods.Select(m => m.Method))
                .Distinct()
                .ToList();
            if (allAsyncMethods.Count > 0)
            {
                var aCols = new[] { "Async Method" }.Concat(labels).ToArray();
                var aRows = allAsyncMethods
                    .Select(m =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var am = s.TopAsyncMethods.FirstOrDefault(x => x.Method == m);
                            return am == default ? "—" : am.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[m, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",",""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(10)
                    .ToList();
                sink.Table(aCols, aRows, "Top async state machine methods across dumps");
            }
        }

        // ── 4. Event Leak Analysis ────────────────────────────────────────────
        sink.Section("4. Event Leak Analysis");
        {
            // Summary counts — always shown (both mini and full mode)
            sink.Table(
                ["Dump", "Total Instances", "Distinct Event Types", "Max on Single Field"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.EventSubscriberTotal > 0 ? s.EventSubscriberTotal.ToString("N0") : "—",
                    s.EventLeakFieldCount  > 0 ? s.EventLeakFieldCount.ToString("N0")  : "—",
                    s.EventLeakMaxOnField  > 0 ? s.EventLeakMaxOnField.ToString("N0")  : "—",
                }).ToList(),
                caption: null);

            // Top event fields — build a union across all dumps so each appears once
            var allFields = snaps
                .SelectMany(s => s.TopEventLeaks)
                .Select(e => (e.PublisherType, e.FieldName))
                .Distinct()
                .ToList();

            if (ignoreEventTypes is { Count: > 0 })
            {
                var before = allFields.Count;
                allFields = allFields
                    .Where(f => !ignoreEventTypes.Any(ig =>
                        f.PublisherType.Contains(ig, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                int removed = before - allFields.Count;
                if (removed > 0)
                    sink.Text($"Filtered out {removed} event field(s) matching: "
                        + string.Join(", ", ignoreEventTypes));
            }

            if (allFields.Count > 0)
            {
                var eventCols = new[] { "Event Type / Field" }.Concat(labels).ToArray();
                var eventRows = allFields
                    .Select(key =>
                    {
                        var perDump = snaps.Select(s =>
                        {
                            var stat = s.TopEventLeaks
                                .FirstOrDefault(e => e.PublisherType == key.PublisherType
                                                  && e.FieldName     == key.FieldName);
                            return stat is null ? "—" : stat.Subscribers.ToString("N0");
                        }).ToArray();
                        return (string[])[$"{key.PublisherType}.{key.FieldName}", .. perDump];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(eventCols, eventRows, "Top event fields across all dumps");
            }
        }

        // ── 5. Finalize Queue Detail ──────────────────────────────────────────
        sink.Section("5. Finalize Queue Detail");
        {
            // Header: Dump | Total
            sink.Table(
                ["Dump", "Total in Queue"],
                snaps.Select((s, i) => new[] { labels[i], s.FinalizerQueueDepth.ToString("N0") }).ToList());

            // Cross-dump breakdown: union of all types seen
            var allFinTypes = snaps
                .SelectMany(s => s.TopFinalizerTypes.Select(t => t.Type))
                .Distinct()
                .ToList();

            if (allFinTypes.Count > 0)
            {
                var finCols = new[] { "Type" }.Concat(labels).ToArray();
                var finRows = allFinTypes
                    .Select(typeName =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var t = s.TopFinalizerTypes.FirstOrDefault(x => x.Type == typeName);
                            return t == default ? "—" : t.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[typeName, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(finCols, finRows, "Top types by peak count");
            }
        }

        // ── 6. Highly Referenced Objects ──────────────────────────────────────
        sink.Section("6. Highly Referenced Objects");
        sink.Text("Reference graph analysis requires a live debugging session or WinDbg/SOS.");
        sink.Text("Use: !gcroot <address>  or  DumpDetective gc-roots <dump> to inspect specific objects.");
        sink.Text("Top types by instance count (proxy for high-fanout objects):");
        {
            var allTopTypes = snaps
                .SelectMany(s => s.TopTypes.Select(t => t.Name))
                .Distinct()
                .ToList();

            var typeCols = new[] { "Type" }.Concat(labels.Select(l => $"{l} Count")).ToArray();
            var typeRows = allTopTypes
                .Select(name =>
                {
                    var counts = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return t is null ? "—" : t.Count.ToString("N0");
                    }).ToArray();
                    return (string[])[name, .. counts];
                })
                .OrderByDescending(r =>
                {
                    long max = 0;
                    for (int i = 1; i < r.Length; i++)
                        if (long.TryParse(r[i].Replace(",", ""), out long v) && v > max) max = v;
                    return max;
                })
                .Take(15)
                .ToList();

            if (typeRows.Count > 0)
                sink.Table(typeCols, typeRows, "Top 15 types by peak instance count across dumps");

            // Top types by size (bytes) — separate table sorted by peak total bytes
            var typeSizeCols = new[] { "Type" }
                .Concat(labels.Select(l => $"{l} Size"))
                .Append("Trend")
                .ToArray();
            var typeSizeRows = snaps
                .SelectMany(s => s.TopTypes.Select(t => t.Name))
                .Distinct()
                .Select(name =>
                {
                    var sizes = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return (raw: t?.TotalBytes ?? 0L, fmt: t is null ? "—" : DumpHelpers.FormatSize(t.TotalBytes));
                    }).ToArray();
                    string trend = Trend(sizes[0].raw, sizes[^1].raw);
                    return (row: (string[])[name, .. sizes.Select(x => x.fmt), trend],
                            peak: sizes.Max(x => x.raw));
                })
                .OrderByDescending(x => x.peak)
                .Take(15)
                .Select(x => x.row)
                .ToList();
            if (typeSizeRows.Count > 0)
                sink.Table(typeSizeCols, typeSizeRows, "Top 15 types by peak total size across dumps");
        }

        // ── 7. Rooted Objects Analysis ────────────────────────────────────────
        sink.Section("7. Rooted Objects Analysis");
        {
            // Summary of handle-kind totals
            sink.Table(
                ["Dump", "Strong", "Pinned", "Weak", "Total"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.StrongHandleCount.ToString("N0"),
                    s.PinnedHandleCount.ToString("N0"),
                    s.WeakHandleCount.ToString("N0"),
                    s.TotalHandleCount.ToString("N0"),
                }).ToList(), "Handle counts per dump");

            // Per-(kind, type) cross-dump breakdown
            var allRootKeys = snaps
                .SelectMany(s => s.TopRootedTypes.Select(r => (r.HandleKind, r.TypeName)))
                .Distinct()
                .ToList();

            if (allRootKeys.Count > 0)
            {
                var rootCols = new[] { "Root Type (Handle Kind)" }.Concat(labels).ToArray();
                var rootRows = allRootKeys
                    .Select(key =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var r = s.TopRootedTypes
                                .FirstOrDefault(x => x.HandleKind == key.HandleKind
                                                  && x.TypeName   == key.TypeName);
                            return r is null ? "—"
                                : $"{r.Count:N0} / {DumpHelpers.FormatSize(r.TotalBytes)}";
                        }).ToArray();
                        return (string[])[$"{key.TypeName} ({key.HandleKind})", .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                        {
                            var cell = r[i];
                            var slash = cell.IndexOf('/');
                            var numPart = slash >= 0 ? cell[..slash].Trim() : cell;
                            if (int.TryParse(numPart.Replace(",", ""), out int v) && v > max) max = v;
                        }
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(rootCols, rootRows, "Top rooted types by peak count  (count / total size)");
            }
        }

        // ── 8. Duplicate String Analysis ─────────────────────────────────────
        sink.Section("8. Duplicate String Analysis");
        if (!full)
        {
            sink.Text("Not collected — re-run with --full to include string duplicate detail.");
        }
        else
        {
            // Summary
            sink.Table(
                ["Dump", "Unique Strings", "Duplicate Groups", "Wasted Memory", "Total String Mem"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.UniqueStringCount.ToString("N0"),
                    s.StringDuplicateGroups.ToString("N0"),
                    DumpHelpers.FormatSize(s.StringWastedBytes),
                    DumpHelpers.FormatSize(s.StringTotalBytes),
                }).ToList());

            // Top duplicated strings across all dumps — union by value
            var allStringVals = snaps
                .SelectMany(s => s.TopStringDuplicates.Select(d => d.Value))
                .Distinct()
                .ToList();

            if (allStringVals.Count > 0)
            {
                var strCols = new[] { "String Value" }
                    .Concat(labels.Select(l => $"{l} Count"))
                    .Concat(labels.Select(l => $"{l} Wasted"))
                    .ToArray();

                var strRows = allStringVals
                    .Select(val =>
                    {
                        var counts  = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : d.Count.ToString("N0");
                        }).ToArray();
                        var wasted  = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : DumpHelpers.FormatSize(d.WastedBytes);
                        }).ToArray();
                        string display = val.Length > 60 ? val[..60] + "…" : val;
                        display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                        return (string[])[$"\"{display}\"", .. counts, .. wasted];
                    })
                    .OrderByDescending(r =>
                    {
                        // Sort by max count across dumps
                        int max = 0;
                        for (int i = 1; i <= snaps.Count; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();

                sink.Table(strCols, strRows, "Top duplicated strings across all dumps");
            }
        }
    }

    static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";

    // Truncates to ≤42 chars so status lines never overflow and cause Spectre box rendering.
    static string ShortName(string path, int max = 42)
    {
        var name = Path.GetFileName(path);
        if (name.Length <= max) return name;
        var ext      = Path.GetExtension(name);
        int keepStem = Math.Max(1, max - ext.Length - 1);
        return name[..keepStem] + "\u2026" + ext;   // e.g. "very-long-dump-filena\u2026.dmp"
    }

    static string Trend(double first, double last, bool higherIsBad = true)
    {
        if (first <= 0) return "~";
        double pct   = (last - first) / first * 100;
        string arrow = pct > 50 ? "↑↑" : pct > 10 ? "↑" : pct < -10 ? "↓" : "~";
        if (higherIsBad && pct > 50) arrow += " ↑↑";
        else if (higherIsBad && pct > 10) arrow += " ↑";
        return arrow;
    }
}
