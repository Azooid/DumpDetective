using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;
using System.Collections.Concurrent;
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
    public static void RenderEmbeddedReports(DumpContext ctx, IRenderSink sink, ProgressLogger? log = null)
    {
        var cmds  = (CommandBase.FullAnalyzeCommandsProvider?.Invoke() ?? []).ToArray();
        int total = cmds.Length;

        var captures = new CaptureSink[total];
        for (int i = 0; i < total; i++) captures[i] = new CaptureSink();

        var overallSw = Stopwatch.StartNew();

        if (log is not null)
        {
            log.BeginParallelBatch("Building sub-reports", total, indent: true);

            // NoBuffering: each thread picks the next available index one-at-a-time
            // in enumeration order, so the LPT ordering in CommandRegistry is honoured.
            Parallel.ForEach(
                Partitioner.Create(Enumerable.Range(0, total), EnumerablePartitionerOptions.NoBuffering),
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                i =>
                {
                    CommandBase.SuppressVerbose = true;
                    CommandBase.BeginTrace();
                    var csw = Stopwatch.StartNew();
                    try
                    {
                        log.StartParallelItem(cmds[i].Name);
                        var doc = cmds[i].BuildReport(ctx);
                        var details = CommandBase.EndTrace();
                        ReportDocReplay.Replay(doc, captures[i]);
                        foreach (var ch in captures[i].GetDoc().Chapters) ch.CommandName ??= cmds[i].Name;
                        csw.Stop();
                        log.CompleteParallelItem(cmds[i].Name, csw.ElapsedMilliseconds, details);
                    }
                    finally { CommandBase.SuppressVerbose = false; }
                });

            log.EndParallelBatch(indent: true);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]  Building sub-reports...[/]");

            AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                .Start(pCtx =>
                {
                    var task = pCtx.AddTask($"[bold]Sub-reports[/]  [dim]0/{total}[/]", maxValue: total);

                    // NoBuffering: each thread picks the next available index one-at-a-time
                    // in enumeration order, so the LPT ordering in CommandRegistry is honoured.
                    Parallel.ForEach(
                        Partitioner.Create(Enumerable.Range(0, total), EnumerablePartitionerOptions.NoBuffering),
                        new ParallelOptions { MaxDegreeOfParallelism = 8 },
                        i =>
                        {
                            CommandBase.SuppressVerbose = true;
                            try
                            {
                                var doc = cmds[i].BuildReport(ctx);
                                ReportDocReplay.Replay(doc, captures[i]);
                                foreach (var ch in captures[i].GetDoc().Chapters) ch.CommandName ??= cmds[i].Name;
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
        }

        for (int i = 0; i < total; i++)
            ReportDocReplay.Replay(captures[i].GetDoc(), sink);
    }

    // ── Scored summary renderer ───────────────────────────────────────────────

    public static void RenderReport(DumpSnapshot s, IRenderSink sink, bool includeHeader = true, DumpContext? ctx = null)
    {
        if (includeHeader)
            sink.Header(
                "Dump Detective — Analysis Report",
                $"{Path.GetFileName(s.DumpPath)}  |  {s.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {s.ClrVersion ?? "unknown"}  |  Score: {s.HealthScore}/100");

        // Findings
        sink.Section("Findings");
        sink.KeyValues(
        [
            ("Health score", $"{s.HealthScore}/100  [{ScoreLabel(s.HealthScore)}]"),
            ("Mode",         s.IsFullMode ? "Full" : "Lightweight"),
        ]);

        if (s.Findings.Count > 0)
        {
            // Build evidence string from snapshot data for each finding
            static string Evidence(Finding f, DumpSnapshot s)
            {
                string h = f.Headline.ToLowerInvariant();
                return f.Category switch
                {
                    "Memory" when h.Contains("finalizer") =>
                        s.TopFinalizerTypes.Count > 0
                            ? string.Join("; ", s.TopFinalizerTypes.Take(3).Select(t => $"{t.Name.Split('.').Last()} ×{t.Count:N0}"))
                            : $"{s.FinalizerQueueDepth:N0} objects queued",
                    "Memory" when h.Contains("heap") || h.Contains("large") =>
                        $"Gen0: {FormatSize(s.Gen0Bytes)}  Gen1: {FormatSize(s.Gen1Bytes)}  Gen2: {FormatSize(s.Gen2Bytes)}  LOH: {FormatSize(s.LohBytes)}",
                    "Memory" when h.Contains("loh") =>
                        $"LOH: {FormatSize(s.LohBytes)}  |  {s.LohObjectCount:N0} objects  |  frag: {s.LohFragmentationPct:F1}%",
                    "Memory" when h.Contains("fragment") =>
                        $"{s.FragmentationPct:F1}% free  ({FormatSize(s.HeapFreeBytes)} of {FormatSize(s.TotalHeapBytes)})",
                    "Memory" when h.Contains("pinned") =>
                        $"{s.PinnedHandleCount:N0} pinned handles",
                    "Memory" when h.Contains("string") =>
                        $"{FormatSize(s.StringWastedBytes)} wasted  |  {s.UniqueStringCount:N0} unique strings",
                    "Memory Leak" =>
                        !string.IsNullOrEmpty(f.Detail) ? f.Detail
                        : s.Gen2Bytes > 0 ? $"Gen2: {FormatSize(s.Gen2Bytes)}  ({(s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0):F0}% of heap)" : "",
                    "Async" =>
                        !string.IsNullOrEmpty(f.Detail) ? f.Detail
                        : $"{s.AsyncBacklogTotal:N0} continuations pending",
                    "Threading" when h.Contains("block") =>
                        $"{s.BlockedThreadCount:N0} blocked  |  {s.ThreadCount:N0} total threads",
                    "Threading" =>
                        $"{s.TpActiveWorkers}/{s.TpMaxWorkers} workers active",
                    "Connections" =>
                        $"{s.ConnectionCount:N0} connections on heap",
                    "Leaks" when h.Contains("event") =>
                        !string.IsNullOrEmpty(f.Detail) ? f.Detail
                        : $"{s.EventSubscriberTotal:N0} subscribers  |  {s.EventLeakFieldCount:N0} fields",
                    "Leaks" when h.Contains("timer") =>
                        $"{s.TimerCount:N0} timer objects",
                    "Exceptions" =>
                        $"{s.ExceptionThreadCount:N0} threads  |  top: {(s.ExceptionCounts.FirstOrDefault() is { } e ? $"{e.Name.Split('.').Last()} ×{e.Count:N0}" : "—")}",
                    "WCF" =>
                        $"{s.WcfFaultedCount:N0} faulted  |  {s.WcfObjectCount:N0} total WCF objects",
                    _ => f.Detail ?? "",
                };
            }

            var findingRows = s.Findings
                .OrderBy(f => f.Severity == FindingSeverity.Critical ? 0 : f.Severity == FindingSeverity.Warning ? 1 : 2)
                .ThenBy(f => f.Category)
                .Select(f =>
                {
                    string sev = f.Severity == FindingSeverity.Critical ? "✗ Critical"
                               : f.Severity == FindingSeverity.Warning  ? "⚠ Warning"
                               :                                          "ℹ Info";
                    string evidence = Evidence(f, s);
                    if (evidence.Length > 90) evidence = evidence[..87] + "…";
                    string advice = string.IsNullOrEmpty(f.Advice) ? "" : f.Advice.Length > 80 ? f.Advice[..77] + "…" : f.Advice;
                    string headline = f.Headline.Length > 75 ? f.Headline[..72] + "…" : f.Headline;
                    return new[] { sev, f.Category, headline, evidence, advice };
                })
                .ToList();
            int critCount = s.Findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warnCount = s.Findings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = s.Findings.Count - critCount - warnCount;
            string caption = $"{s.Findings.Count} finding(s)";
            if (critCount > 0) caption += $"  |  ✗ {critCount} critical";
            if (warnCount > 0) caption += $"  |  ⚠ {warnCount} warning";
            if (infoCount  > 0) caption += $"  |  ℹ {infoCount} info";
            sink.Table(["Severity", "Category", "Finding", "Evidence", "Recommendation"], findingRows, caption);
        }
        else
        {
            sink.Alert(AlertLevel.Info, "No findings — all monitored signals within acceptable thresholds.");
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
        if (s.ExceptionCounts.Count > 0 || (ctx is not null && ctx.Runtime.Threads.Any(t => t.CurrentException is not null)))
        {
            sink.Section("Exceptions on Heap");

            // Summary table from snapshot
            if (s.ExceptionCounts.Count > 0)
                sink.Table(
                    ["Exception Type", "Count"],
                    s.ExceptionCounts.Take(15).Select(e => new[] { e.Name, e.Count.ToString("N0") }).ToList());

            // Active exceptions with stack traces from live threads
            if (ctx is not null)
            {
                var activeThreads = ctx.Runtime.Threads
                    .Where(t => t.CurrentException is not null)
                    .ToList();

                if (activeThreads.Count > 0)
                {
                    sink.Alert(AlertLevel.Critical,
                        $"{activeThreads.Count} active (in-flight) exception(s) on managed threads — these caused or are related to the crash.");

                    foreach (var t in activeThreads)
                    {
                        var ex = t.CurrentException!;
                        string title = $"Thread {t.ManagedThreadId}  (OS 0x{t.OSThreadId:X4})  —  {ex.Type?.Name ?? "?"}  ⚡ ACTIVE";
                        sink.BeginDetails(title, open: true);

                        var infoRows = new List<string[]>();
                        if (!string.IsNullOrEmpty(ex.Message))     infoRows.Add(["Message",         ex.Message]);
                        if (ex.HResult != 0)                       infoRows.Add(["HResult",         $"0x{ex.HResult:X8}"]);
                        if (ex.Inner?.Type?.Name is string inner)   infoRows.Add(["Inner Exception", inner]);
                        infoRows.Add(["Address", $"0x{ex.Address:X16}"]);
                        sink.Table(["Field", "Value"], infoRows);

                        // Original throw stack from ClrException.StackTrace
                        var throwFrames = ex.StackTrace
                            .Select(f => f.ToString() ?? "")
                            .Where(f => f.Length > 0)
                            .ToList();
                        if (throwFrames.Count > 0)
                            sink.Table(["Stack Frame"], throwFrames.Select(f => new[] { f }).ToList(),
                                "Original throw stack — where the exception was raised");
                        else
                            sink.Text("⚠ Original throw stack not available.");

                        // Live thread call stack
                        var threadFrames = t.EnumerateStackTrace(includeContext: false)
                            .Select(f => f.ToString() ?? "")
                            .Where(f => f.Length > 0)
                            .ToList();
                        if (threadFrames.Count > 0)
                            sink.Table(["Stack Frame"], threadFrames.Select(f => new[] { f }).ToList(),
                                "Live thread call stack — where the thread was when the dump was captured");

                        sink.EndDetails();
                    }
                }
            }
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
