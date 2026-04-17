using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Renders the scored health summary and embedded full sub-reports for a single dump.
/// Called by <c>AnalyzeCommand</c> and <c>TrendAnalysisCommand</c>.
/// </summary>
public static class AnalyzeReport
{
    // ── Sub-reports (full mode) ───────────────────────────────────────────────

    /// <summary>
    /// Runs all <see cref="ICommand.IncludeInFullAnalyze"/> commands in parallel,
    /// captures each to its own <see cref="CaptureSink"/>, then replays in order.
    /// </summary>
    public static void RenderEmbeddedReports(DumpContext ctx, IRenderSink sink)
    {
        AnsiConsole.MarkupLine("[dim]  Building sub-reports...[/]");

        var cmds  = (CommandBase.FullAnalyzeCommandsProvider?.Invoke() ?? []).ToArray();
        int total = cmds.Length;

        var captures = new CaptureSink[total];
        for (int i = 0; i < total; i++) captures[i] = new CaptureSink();

        var overallSw = Stopwatch.StartNew();
        AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
            .Start(pCtx =>
            {
                var task = pCtx.AddTask($"[bold]Sub-reports[/]  [dim]0/{total}[/]", maxValue: total);

                Parallel.For(0, total,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    i =>
                    {
                        CommandBase.SuppressVerbose = true;
                        try
                        {
                            var doc = cmds[i].BuildReport(ctx);
                            ReportDocReplay.Replay(doc, captures[i]);
                            task.Increment(1);
                            int n = (int)task.Value;
                            task.Description = n >= total
                                ? $"[bold]Sub-reports[/]  [dim]{total}/{total}  Done[/]"
                                : $"[bold]Sub-reports[/]  [dim]{n}/{total}  {Markup.Escape(cmds[i].Description)}[/]";
                        }
                        finally
                        {
                            CommandBase.SuppressVerbose = false;
                        }
                    });
            });

        AnsiConsole.MarkupLine($"[dim]  ✓ {total}/{total} sub-reports  ({overallSw.Elapsed.TotalSeconds:F1}s)[/]");

        for (int i = 0; i < total; i++)
            ReportDocReplay.Replay(captures[i].GetDoc(), sink);
    }

    // ── Scored summary renderer ───────────────────────────────────────────────

    public static void RenderReport(DumpSnapshot s, IRenderSink sink, bool includeHeader = true)
    {
        if (includeHeader)
            sink.Header(
                "Dump Detective — Analysis Report",
                $"{Path.GetFileName(s.DumpPath)}  |  {s.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {s.ClrVersion ?? "unknown"}  |  Score: {s.HealthScore}/100");

        // Findings
        sink.Section("Findings");
        foreach (var f in s.Findings)
        {
            var lvl = f.Severity switch
            {
                FindingSeverity.Critical => AlertLevel.Critical,
                FindingSeverity.Warning  => AlertLevel.Warning,
                _                        => AlertLevel.Info,
            };
            sink.Alert(lvl, $"[{f.Category}] {f.Headline}", f.Detail, f.Advice);
        }
        sink.KeyValues(
        [
            ("Health score", $"{s.HealthScore}/100  [{ScoreLabel(s.HealthScore)}]"),
            ("Mode",         s.IsFullMode ? "Full" : "Lightweight"),
        ]);

        var breakdownRows = s.Findings
            .Where(f => f.Deduction > 0)
            .OrderByDescending(f => f.Deduction)
            .Select(f => new[]
            {
                f.Category,
                f.Headline.Length > 65 ? f.Headline[..62] + "…" : f.Headline,
                $"-{f.Deduction}",
            })
            .ToList();
        if (breakdownRows.Count > 0)
        {
            int totalDeducted = 100 - s.HealthScore;
            sink.Table(["Category", "Finding", "Pts Deducted"], breakdownRows,
                $"Score breakdown: {s.HealthScore}/100 (−{totalDeducted} total)");
        }

        // Memory
        sink.Section("Memory");
        sink.KeyValues(
        [
            ("Total heap",    FormatSize(s.TotalHeapBytes)),
            ("Gen0",          FormatSize(s.Gen0Bytes)),
            ("Gen1",          FormatSize(s.Gen1Bytes)),
            ("Gen2",          FormatSize(s.Gen2Bytes)),
            ("LOH",           FormatSize(s.LohBytes)),
            ("POH",           FormatSize(s.PohBytes)),
            ("Fragmentation", $"{s.FragmentationPct:F1}%"),
        ]);
        if (s.TopTypes.Count > 0)
            sink.Table(
                ["Type", "Count", "Total Size"],
                s.TopTypes.Take(25).Select(t =>
                    new[] { t.Name, t.Count.ToString("N0"), FormatSize(t.TotalBytes) }).ToList(),
                "Top types by size");

        // Threads
        sink.Section("Threads & Thread Pool");
        sink.KeyValues(
        [
            ("Total threads",  s.ThreadCount.ToString("N0")),
            ("Blocked",        s.BlockedThreadCount.ToString("N0")),
            ("With exception", s.ExceptionThreadCount.ToString("N0")),
            ("TP active",      $"{s.TpActiveWorkers} / {s.TpMaxWorkers} max"),
            ("TP idle",        s.TpIdleWorkers.ToString()),
        ]);
        if (s.AsyncBacklogTotal > 0)
        {
            sink.KeyValues([("Async backlog", s.AsyncBacklogTotal.ToString("N0"))]);
            if (s.TopAsyncMethods.Count > 0)
                sink.Table(
                    ["Method", "Count"],
                    s.TopAsyncMethods.Take(10).Select(m => new[] { m.Name, m.Count.ToString("N0") }).ToList(),
                    "Top suspended async methods");
        }

        // Exceptions
        if (s.ExceptionCounts.Count > 0)
        {
            sink.Section("Exceptions on Heap");
            sink.Table(
                ["Exception Type", "Count"],
                s.ExceptionCounts.Take(15).Select(e => new[] { e.Name, e.Count.ToString("N0") }).ToList());
        }

        // Leaks & Handles
        sink.Section("Leaks & Handles");
        sink.KeyValues(
        [
            ("Finalizer queue", s.FinalizerQueueDepth.ToString("N0")),
            ("Pinned handles",  s.PinnedHandleCount.ToString("N0")),
            ("Weak handles",    s.WeakHandleCount.ToString("N0")),
            ("Strong handles",  s.StrongHandleCount.ToString("N0")),
            ("Timer objects",   s.TimerCount.ToString("N0")),
            ("WCF objects",     $"{s.WcfObjectCount:N0}  (faulted: {s.WcfFaultedCount:N0})"),
            ("DB connections",  s.ConnectionCount.ToString("N0")),
        ]);
        if (s.TopFinalizerTypes.Count > 0)
            sink.Table(
                ["Type", "Count"],
                s.TopFinalizerTypes.Select(t => new[] { t.Name, t.Count.ToString("N0") }).ToList(),
                "Top finalizer queue types");

        // Event Leaks (full mode)
        if (s.IsFullMode)
        {
            sink.Section("Event Leaks");
            sink.KeyValues(
            [
                ("Leak fields",       s.EventLeakFieldCount.ToString("N0")),
                ("Total subscribers", s.EventSubscriberTotal.ToString("N0")),
                ("Max on one field",  s.EventLeakMaxOnField.ToString("N0")),
            ]);
            if (s.TopEventLeaks.Count > 0)
                sink.Table(
                    ["Publisher Type", "Field", "Subscribers"],
                    s.TopEventLeaks.Select(e =>
                        new[] { e.PublisherType, e.FieldName, e.Subscribers.ToString("N0") }).ToList(),
                    "Top event leak fields");

            sink.Section("String Duplicates");
            sink.KeyValues(
            [
                ("Duplicate groups",   s.StringDuplicateGroups.ToString("N0")),
                ("Wasted bytes",       FormatSize(s.StringWastedBytes)),
                ("Total string bytes", FormatSize(s.StringTotalBytes)),
            ]);
        }

        // Modules
        sink.Section("Modules");
        sink.KeyValues(
        [
            ("Total assemblies", s.ModuleCount.ToString("N0")),
            ("App assemblies",   s.AppModuleCount.ToString("N0")),
            ("System/framework", (s.ModuleCount - s.AppModuleCount).ToString("N0")),
        ]);

        // Memory Leak Summary
        sink.Section("Memory Leak Analysis");
        {
            double gen2Pct = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;
            var strType    = s.TopTypes.FirstOrDefault(t => t.Name == "System.String");

            sink.KeyValues(
            [
                ("Gen2 % of heap", $"{gen2Pct:F1}%  ({FormatSize(s.Gen2Bytes)})"),
                ("LOH",            FormatSize(s.LohBytes)),
                ("Total objects",  s.TotalObjectCount.ToString("N0")),
                ("System.String",  strType is not null
                                       ? $"{strType.Count:N0}  ({FormatSize(strType.TotalBytes)})"
                                       : "—"),
            ]);

            if (gen2Pct > 50)
                sink.Alert(AlertLevel.Critical,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap",
                    detail: "Objects are surviving multiple GC cycles — strong managed memory leak signal.",
                    advice: "Run: memory-leak <dump>  for full suspect analysis with GC root chains.");
            else if (gen2Pct > 30)
                sink.Alert(AlertLevel.Warning,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap",
                    detail: "Gen2 is elevated. Monitor for growth across multiple dumps.",
                    advice: "Run: trend-analysis <d1> <d2> to confirm growth, then memory-leak <dump>.");
            else
                sink.Alert(AlertLevel.Info,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap — within normal range.",
                    advice: "Run: memory-leak <dump>  for a deeper investigation if growth is suspected.");

            var suspects = s.TopTypes
                .Where(t => t.Count >= 1_000 || t.TotalBytes >= 1_000_000)
                .OrderByDescending(t => t.Count)
                .Take(15)
                .ToList();

            if (suspects.Count > 0)
                sink.Table(
                    ["Type", "Count", "Total Size"],
                    suspects.Select(t =>
                        new[] { t.Name, t.Count.ToString("N0"), FormatSize(t.TotalBytes) }).ToList(),
                    "High-count / large types — accumulating types are leak suspects. " +
                    "Run 'memory-leak <dump>' for full Gen2/LOH breakdown + GC root chains.");
        }
    }

    public static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";

    private static string FormatSize(long b) => DumpHelpers.FormatSize(b);
}
