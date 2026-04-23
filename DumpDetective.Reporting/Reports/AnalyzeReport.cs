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

        // ── Findings ─────────────────────────────────────────────────────────
        sink.Section("Findings");

        sink.Explain(
            what: "This section lists all diagnostic signals detected in this dump that exceeded configured thresholds. " +
                  "Each finding represents a measurable condition that is outside the healthy range for .NET application behavior.",
            why:  "Findings are the fastest way to identify what is wrong. They are prioritized by severity so " +
                  "critical conditions are always investigated before warnings. Without this, you would need to " +
                  "manually inspect dozens of raw metrics across all sections.",
            bullets: BuildFindingsBullets(s),
            impact: ScoreImpact(s.HealthScore),
            action: s.Findings.Count > 0
                ? "Start from the Critical findings. Each row includes a Recommendation column pointing to the " +
                  "most effective next diagnostic step."
                : null);

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

            // Severity legend
            sink.Alert(AlertLevel.Info,
                "Severity definitions",
                detail: "✗ Critical — likely affecting stability, scalability, or memory health right now. " +
                        "⚠ Warning — may cause performance degradation or operational instability under load. " +
                        "ℹ Info — observed but unlikely to impact runtime stability alone.");
        }
        else
        {
            sink.Alert(AlertLevel.Info, "No findings — all monitored signals within acceptable thresholds.");
        }

        // Cross-metric narrative based on snapshot data
        string? narrative = BuildCrossMetricNarrative(s);
        if (narrative is not null)
            sink.Alert(AlertLevel.Info, "Diagnostic interpretation", detail: narrative);

        // ── Memory ───────────────────────────────────────────────────────────
        sink.Section("Memory");
        sink.Explain(
            what: "Managed heap memory is divided into generations. Gen0 holds newly allocated short-lived objects. " +
                  "Gen1 is an intermediate buffer. Gen2 holds long-lived objects that survived multiple GC cycles. " +
                  "LOH (Large Object Heap) holds objects over 85 KB — it is rarely compacted.",
            why:  "The distribution of memory across generations reveals how the garbage collector is managing your objects. " +
                  "A growing Gen2 or LOH is the most reliable signal of a memory leak.",
            bullets:
            [
                "Gen2 > 50% of total heap → strong memory leak signal — objects accumulating without release",
                "LOH growing across dumps → large buffers or arrays not being released",
                "High fragmentation → increased allocation pressure and potential OutOfMemoryException",
                "Healthy apps: Gen0 is large, Gen2 is small relative to total heap",
            ],
            impact: "If Gen2 continues to grow, the application will eventually exhaust available memory, " +
                    "causing increasingly long GC pauses, request latency spikes, and eventually OutOfMemoryException.");
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
        // Dynamic interpretation of memory distribution
        if (s.TotalHeapBytes > 0)
        {
            double gen2Pct = s.Gen2Bytes * 100.0 / s.TotalHeapBytes;
            double lohPct  = s.LohBytes  * 100.0 / s.TotalHeapBytes;
            if (gen2Pct > 50)
                sink.Alert(AlertLevel.Critical,
                    $"Gen2 holds {gen2Pct:F1}% of heap — strong retention signal",
                    detail: "Objects are surviving repeated GC cycles. This indicates persistent long-lived references " +
                            "preventing garbage collection. The application is likely retaining more memory over time.",
                    advice: "Run 'memory-leak <dump>' to identify which object types are accumulating and trace their GC root chains.");
            else if (gen2Pct > 30)
                sink.Alert(AlertLevel.Warning,
                    $"Gen2 holds {gen2Pct:F1}% of heap — elevated",
                    detail: "Gen2 is above the typical healthy range. This may indicate slow leak accumulation. " +
                            "Compare across multiple dumps to confirm growth.",
                    advice: "Run 'trend-analysis <d1> <d2>' to confirm growth, then 'memory-leak <dump>' for root cause analysis.");
            if (lohPct > 20)
                sink.Alert(AlertLevel.Warning,
                    $"LOH holds {lohPct:F1}% of heap",
                    detail: "Large Object Heap is elevated. The LOH is rarely compacted — large objects, arrays, " +
                            "or buffers that are retained here contribute to long-term memory pressure.",
                    advice: "Run 'large-objects <dump>' to identify which large objects are being retained.");
            if (s.FragmentationPct > 30)
                sink.Alert(AlertLevel.Warning,
                    $"Heap fragmentation is {s.FragmentationPct:F1}%",
                    detail: $"{FormatSize(s.HeapFreeBytes)} is free space fragmented between live objects. " +
                            "High fragmentation forces the GC to do more work and can cause allocation failures " +
                            "even when total free space appears adequate.",
                    advice: "Run 'heap-fragmentation <dump>' and 'pinned-objects <dump>' to identify pinned handles causing fragmentation.");
        }
        if (s.TopTypes.Count > 0)
            sink.Table(
                ["Type", "Count", "Total Size"],
                s.TopTypes.Take(25).Select(t =>
                    new[] { t.Name, t.Count.ToString("N0"), FormatSize(t.TotalBytes) }).ToList(),
                "Top types by size — types with very high counts or large footprints are the primary leak investigation targets");

        // ── Threads & Thread Pool ─────────────────────────────────────────────
        sink.Section("Threads & Thread Pool");
        sink.Explain(
            what: "This section shows the current state of all managed threads and the .NET thread pool at the time " +
                  "the dump was captured. It reveals whether the application is under execution pressure.",
            why:  "Blocked threads indicate lock contention or I/O stalls. Low idle thread pool workers mean " +
                  "new work items queue up instead of executing immediately — this causes response time degradation. " +
                  "A large async backlog means async continuations are not completing in time.",
            bullets:
            [
                "Blocked threads > 5 → investigate for deadlocks or slow I/O (run 'deadlock-detection <dump>')",
                "TP idle workers near 0 → thread pool starvation — async work will be delayed",
                "Async backlog > 1,000 → downstream bottleneck or thread pool starvation",
                "Threads with exceptions → may indicate crash-related or unhandled exception conditions",
            ],
            impact: "Thread pool starvation causes all async operations to queue up. Request processing slows " +
                    "dramatically. Under sustained starvation, the application can appear completely unresponsive " +
                    "even though the CPU and memory are not fully saturated.");
        sink.KeyValues(
        [
            ("Total threads",  s.ThreadCount.ToString("N0")),
            ("Blocked",        s.BlockedThreadCount.ToString("N0")),
            ("With exception", s.ExceptionThreadCount.ToString("N0")),
            ("TP active",      $"{s.TpActiveWorkers} / {s.TpMaxWorkers} max"),
            ("TP idle",        s.TpIdleWorkers.ToString()),
        ]);
        if (s.BlockedThreadCount > 5)
            sink.Alert(AlertLevel.Warning,
                $"{s.BlockedThreadCount} blocked threads detected",
                detail: "A significant number of threads are waiting on locks, I/O, or synchronization primitives. " +
                        "This reduces available parallelism and can contribute to throughput degradation.",
                advice: "Run 'thread-analysis <dump>' for detailed stack traces of blocked threads. " +
                        "Run 'deadlock-detection <dump>' to check for circular wait cycles.");
        if (s.TpIdleWorkers == 0 && s.TpMaxWorkers > 0)
            sink.Alert(AlertLevel.Warning,
                "Thread pool has no idle workers",
                detail: "All thread pool workers are actively executing or the pool is fully saturated. " +
                        "New work items will queue and may cause measurable latency spikes.",
                advice: "Run 'thread-pool-starvation <dump>' to diagnose the thread pool queue depth and worker exhaustion.");
        if (s.AsyncBacklogTotal > 0)
        {
            if (s.AsyncBacklogTotal > 5_000)
                sink.Alert(AlertLevel.Critical,
                    $"Async backlog: {s.AsyncBacklogTotal:N0} pending continuations",
                    detail: "A critically large number of async state machines are suspended waiting to resume. " +
                            "This indicates a downstream bottleneck — I/O stalls, lock contention, or thread pool starvation — " +
                            "is preventing async work from completing.",
                    advice: "Run 'async-stacks <dump>' to identify which async methods are blocking and how many continuations are queued.");
            else if (s.AsyncBacklogTotal > 500)
                sink.Alert(AlertLevel.Warning,
                    $"Async backlog: {s.AsyncBacklogTotal:N0} pending continuations",
                    advice: "Run 'async-stacks <dump>' for a breakdown of suspended async methods.");
            sink.KeyValues([("Async backlog", s.AsyncBacklogTotal.ToString("N0"))]);
            if (s.TopAsyncMethods.Count > 0)
                sink.Table(
                    ["Method", "Count"],
                    s.TopAsyncMethods.Take(10).Select(m => new[] { m.Name, m.Count.ToString("N0") }).ToList(),
                    "Top suspended async methods — methods with high counts are awaiting completion at a bottleneck");
        }

        // ── Exceptions ────────────────────────────────────────────────────────
        if (s.ExceptionCounts.Count > 0 || (ctx is not null && ctx.Runtime.Threads.Any(t => t.CurrentException is not null)))
        {
            sink.Section("Exceptions on Heap");
            sink.Explain(
                what: "Exceptions found in the managed heap (created but not yet collected) and active exceptions on thread stacks.",
                why:  "Exception objects on the heap indicate error conditions that occurred during the application's lifetime. " +
                      "Active exceptions on threads are the most important — they may have caused or be related to the crash.",
                bullets:
                [
                    "Active exceptions on thread stacks (⚡ ACTIVE) → these caused or are directly related to the problem",
                    "High exception counts on heap → the application is throwing exceptions frequently, degrading throughput",
                    "OutOfMemoryException → the application ran out of memory at least once",
                    "StackOverflowException → recursive loop or deep call chain — may indicate a logic error",
                ],
                impact: "Frequent exceptions degrade throughput significantly. Each exception allocates memory, " +
                        "captures a stack trace, and interrupts the normal execution path. " +
                        "Active exceptions on thread stacks indicate the application was in a failed state at capture time.");

            if (s.ExceptionCounts.Count > 0)
                sink.Table(
                    ["Exception Type", "Count"],
                    s.ExceptionCounts.Take(15).Select(e => new[] { e.Name, e.Count.ToString("N0") }).ToList());

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

                        var throwFrames = ex.StackTrace
                            .Select(f => f.ToString() ?? "")
                            .Where(f => f.Length > 0)
                            .ToList();
                        if (throwFrames.Count > 0)
                            sink.Table(["Stack Frame"], throwFrames.Select(f => new[] { f }).ToList(),
                                "Original throw stack — where the exception was raised");
                        else
                            sink.Text("⚠ Original throw stack not available.");

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

        // ── Leaks & Handles ───────────────────────────────────────────────────
        sink.Section("Leaks & Handles");
        sink.Explain(
            what: "Resource and handle tracking: finalizer queue depth, GC handles, timers, WCF channels, and database connections.",
            why:  "These metrics identify resources that were created but not properly released. Unlike managed memory leaks, " +
                  "resource leaks can exhaust system-level limits — connection pool exhaustion, handle limit errors, " +
                  "or native resource pressure — before memory pressure becomes visible.",
            bullets:
            [
                "Large finalizer queue → objects with Finalize() waiting for cleanup — IDisposable not being called",
                "High pinned handles → objects pinned for native interop, causing heap fragmentation",
                "Many timers → Timer objects not being disposed — their callbacks and closures remain alive",
                "WCF faulted channels → WCF connections in error state not being properly closed",
                "High DB connections → connection pool under pressure or connections not being returned",
            ],
            impact: "Resource leaks can cause: connection pool exhaustion (new DB requests fail), " +
                    "native handle limit errors, WCF communication failures, and finalizer thread backup " +
                    "which prevents GC from reclaiming memory efficiently.");
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
        if (s.FinalizerQueueDepth >= 500)
            sink.Alert(AlertLevel.Critical,
                $"Finalizer queue: {s.FinalizerQueueDepth:N0} objects pending cleanup",
                detail: "A large finalizer queue means hundreds of objects are waiting for their Finalize() " +
                        "method to run. The finalizer thread is a single thread — a backlog here delays all " +
                        "resource cleanup and prevents those objects' memory from being reclaimed.",
                advice: "Run 'finalizer-queue <dump>' to see which types are in the queue. " +
                        "Audit IDisposable usage — call Dispose() or use 'using' statements to avoid finalizer pressure.");
        else if (s.FinalizerQueueDepth >= 100)
            sink.Alert(AlertLevel.Warning,
                $"Finalizer queue: {s.FinalizerQueueDepth:N0} objects pending cleanup",
                advice: "Run 'finalizer-queue <dump>' for type breakdown.");
        if (s.WcfFaultedCount > 0)
            sink.Alert(AlertLevel.Warning,
                $"{s.WcfFaultedCount:N0} faulted WCF channel(s) detected",
                detail: "Faulted WCF channels must be explicitly closed with Abort() — they cannot be reused. " +
                        "Accumulated faulted channels retain memory and their associated resources.",
                advice: "Run 'wcf-channels <dump>' for channel state details. Ensure WCF clients call Abort() on faulted channels.");
        if (s.TopFinalizerTypes.Count > 0)
            sink.Table(
                ["Type", "Count"],
                s.TopFinalizerTypes.Select(t => new[] { t.Name, t.Count.ToString("N0") }).ToList(),
                "Top finalizer queue types — types with high counts are the primary IDisposable violation suspects");

        // ── Event Leaks (full mode) ───────────────────────────────────────────
        if (s.IsFullMode)
        {
            sink.Section("Event Leaks");
            sink.Explain(
                what: "Event handler subscriptions found on the heap. An event leak occurs when subscriber objects " +
                      "remain referenced by publishers after the subscriber should have been released.",
                why:  "Because the publisher still holds delegate references pointing to the subscriber, " +
                      "the garbage collector cannot reclaim the subscriber object graph. A single long-lived " +
                      "publisher — especially a static one — can silently retain thousands of objects indefinitely.",
                bullets:
                [
                    "Static publishers → subscribers on that event will NEVER be garbage collected",
                    "Growing subscriber count across dumps → subscriptions accumulating without unsubscription",
                    "High retained bytes → large object graphs are being kept alive by event references",
                    "Lambda/closure subscribers → the closure captures additional objects in its scope",
                ],
                impact: "Event leaks cause silent, unbounded memory growth. They are particularly dangerous because " +
                        "the retained objects appear to be 'in use' from the GC's perspective — no tool will flag them " +
                        "as garbage. The only resolution is fixing the subscription lifecycle.",
                action: "Audit event subscription ownership. Ensure -=  is called in Dispose(), during shutdown, " +
                        "or use WeakEventManager/IDisposable patterns. Run 'event-analysis <dump>' for full detail.");
            sink.KeyValues(
            [
                ("Leak fields",       s.EventLeakFieldCount.ToString("N0")),
                ("Total subscribers", s.EventSubscriberTotal.ToString("N0")),
                ("Max on one field",  s.EventLeakMaxOnField.ToString("N0")),
            ]);
            if (s.EventLeakMaxOnField > 1_000)
                sink.Alert(AlertLevel.Critical,
                    $"Single event field has {s.EventLeakMaxOnField:N0} subscribers",
                    detail: "A single event field with over 1,000 subscribers indicates a severe subscription leak. " +
                            "This is likely a singleton or static publisher accumulating subscriptions over time.",
                    advice: "Run 'event-analysis <dump>' to identify the publisher and field. Review all += call sites.");
            if (s.TopEventLeaks.Count > 0)
                sink.Table(
                    ["Publisher Type", "Field", "Subscribers"],
                    s.TopEventLeaks.Select(e =>
                        new[] { e.PublisherType, e.FieldName, e.Subscribers.ToString("N0") }).ToList(),
                    "Top event leak fields — highest subscriber counts indicate the most severe leak sources");

            sink.Section("String Duplicates");
            sink.Explain(
                what: "Identical string values stored in multiple separate String objects on the heap.",
                why:  "Duplicate strings waste memory — each copy occupies heap space independently. " +
                      "A large number of duplicates may indicate repeated serialization, redundant cache entries, " +
                      "or data patterns that could be consolidated.",
                bullets:
                [
                    "High wasted bytes → significant memory could be reclaimed by deduplication",
                    "Common in HTTP servers: request URLs, header values, JSON keys repeated per-request",
                    "Connection strings and configuration values often duplicated per-component",
                ],
                action: "Run 'string-duplicates <dump>' for the full duplicate list. " +
                        "Consider string.Intern(), shared constants, or a string pool for high-frequency values.");
            sink.KeyValues(
            [
                ("Duplicate groups",   s.StringDuplicateGroups.ToString("N0")),
                ("Wasted bytes",       FormatSize(s.StringWastedBytes)),
                ("Total string bytes", FormatSize(s.StringTotalBytes)),
            ]);
        }

        // ── Modules ───────────────────────────────────────────────────────────
        sink.Section("Modules");
        sink.KeyValues(
        [
            ("Total assemblies", s.ModuleCount.ToString("N0")),
            ("App assemblies",   s.AppModuleCount.ToString("N0")),
            ("System/framework", (s.ModuleCount - s.AppModuleCount).ToString("N0")),
        ]);

        // ── Memory Leak Analysis ──────────────────────────────────────────────
        sink.Section("Memory Leak Analysis");
        sink.Explain(
            what: "A focused analysis of memory retention patterns. Gen2 percentage and LOH usage are the primary " +
                  "indicators of whether the application has a managed memory leak.",
            why:  "Unlike native leaks, managed memory leaks are caused by objects that are still referenced — " +
                  "either intentionally (caches, static fields) or accidentally (forgotten event handlers, " +
                  "static collections, long-lived closures). The GC cannot reclaim any object that is reachable from a root.",
            bullets:
            [
                "Gen2 > 50% → critical leak signal: objects are surviving multiple GC cycles",
                "Gen2 > 30% → elevated: monitor across dumps to confirm growth",
                "LOH growth → large buffers or datasets not being released",
                "High-count types (> 10,000 instances) → accumulating types are primary suspects",
                "System.String at high count → string duplication or retained string collections",
            ],
            impact: "A managed memory leak will eventually cause OutOfMemoryException. " +
                    "Before that threshold, the application experiences increasing GC pause times, " +
                    "higher CPU usage from garbage collection, and degraded throughput as the GC " +
                    "performs increasingly expensive Gen2 collections.",
            action: "Run 'memory-leak <dump>' for a full suspect analysis including GC root chains. " +
                    "Run 'gc-roots <dump> --type <TypeName>' to trace why specific objects are retained.");
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
                    detail: "Objects are surviving multiple GC cycles — strong managed memory leak signal. " +
                            "The application is retaining object graphs that should have been collected. " +
                            "This will worsen over time without intervention.",
                    advice: "Run: memory-leak <dump>  for full suspect analysis with GC root chains.");
            else if (gen2Pct > 30)
                sink.Alert(AlertLevel.Warning,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap",
                    detail: "Gen2 is elevated. Monitor for growth across multiple dumps. " +
                            "A single elevated snapshot may be normal after a workload peak. " +
                            "Consistent elevation across snapshots is a leak signal.",
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
                    "High-count / large types — types accumulating without release are leak suspects. " +
                    "Run 'memory-leak <dump>' for Gen2/LOH breakdown + GC root chains.");
        }
    }

    // ── Interpretation helpers ────────────────────────────────────────────────

    private static string[] BuildFindingsBullets(DumpSnapshot s)
    {
        int critCount = s.Findings.Count(f => f.Severity == FindingSeverity.Critical);
        int warnCount = s.Findings.Count(f => f.Severity == FindingSeverity.Warning);
        var bullets = new List<string>();
        if (critCount > 0)
            bullets.Add($"✗ {critCount} critical finding(s) — these require immediate investigation");
        if (warnCount > 0)
            bullets.Add($"⚠ {warnCount} warning(s) — review these after addressing critical issues");
        bullets.Add("Each row includes Evidence (measured values) and a Recommendation pointing to the next step");
        bullets.Add("A finding means the metric exceeded its configured threshold at the time of capture");
        bullets.Add("A stable but critically elevated metric may still represent a severe unresolved issue");
        return [.. bullets];
    }

    private static string ScoreImpact(int score) => score switch
    {
        < 40 => "The application is in a critically degraded state. One or more conditions are likely affecting " +
                "stability, scalability, or memory health right now. Immediate investigation is warranted.",
        < 70 => "The application shows signs of degradation. Performance may be impacted and conditions could " +
                "worsen under additional load. Investigation is recommended before the next production deployment.",
        _    => "The application appears healthy. All monitored signals are within acceptable ranges. " +
                "Continue monitoring across multiple dumps to confirm stability.",
    };

    private static string? BuildCrossMetricNarrative(DumpSnapshot s)
    {
        double gen2Pct = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;
        bool highGen2 = gen2Pct > 30;
        bool highFinalizer = s.FinalizerQueueDepth >= 100;
        bool highAsync = s.AsyncBacklogTotal > 500;
        bool highBlocked = s.BlockedThreadCount > 5;
        bool highEventLeaks = s.EventSubscriberTotal > 500;
        bool threadPoolPressure = s.TpIdleWorkers == 0 && s.TpMaxWorkers > 0;

        var parts = new List<string>();

        if (highGen2 && highFinalizer)
            parts.Add($"Gen2 memory retention ({gen2Pct:F1}%) combined with a large finalizer queue ({s.FinalizerQueueDepth:N0} objects) " +
                      "suggests objects are not being disposed properly — they are surviving to Gen2 and then queuing for finalization instead of " +
                      "being collected immediately via Dispose().");

        if (highAsync && threadPoolPressure)
            parts.Add($"A high async backlog ({s.AsyncBacklogTotal:N0} continuations) combined with no idle thread pool workers indicates " +
                      "async work is completing slowly because worker threads are exhausted. New continuations queue up faster than they are processed.");

        if (highAsync && highBlocked)
            parts.Add($"Both async backlog ({s.AsyncBacklogTotal:N0}) and blocked threads ({s.BlockedThreadCount:N0}) are elevated. " +
                      "This pattern often indicates lock contention: async operations are awaiting locks held by blocked threads.");

        if (highGen2 && highEventLeaks)
            parts.Add($"Gen2 retention and event subscriber accumulation ({s.EventSubscriberTotal:N0} total subscribers) are both elevated. " +
                      "Event leaks are a common cause of Gen2 growth — the subscriber object graphs cannot be collected because " +
                      "the publisher's event field still references them.");

        if (highFinalizer && !highGen2)
            parts.Add($"The finalizer queue is elevated ({s.FinalizerQueueDepth:N0} objects) but Gen2 memory is within range. " +
                      "This may indicate heavy IDisposable usage without explicit Dispose() calls. The objects are being finalized " +
                      "rather than disposed, adding pressure to the finalizer thread.");

        if (parts.Count == 0) return null;
        return string.Join(" — ", parts);
    }

    public static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";

    private static string FormatSize(long b) => DumpHelpers.FormatSize(b);
}
