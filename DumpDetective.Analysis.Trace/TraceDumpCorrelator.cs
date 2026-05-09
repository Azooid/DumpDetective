using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Cross-source correlation engine: evaluates causal rules across trace analyzer
/// outputs AND a dump snapshot captured at the same point in time.
///
/// Use this when you have both a .etl/.nettrace file AND a .dmp file taken
/// during the same incident window. Each rule fires only when both contributing
/// signals are present (trace side + dump side), yielding higher-confidence
/// diagnoses than either source alone.
///
/// Rules produce <see cref="CorrelationFinding"/> records — same type as
/// <see cref="CorrelationEngine"/> so the report layer can render them identically.
/// ContributingAreas includes "dump" alongside the relevant trace sub-analyzer names.
/// </summary>
public static class TraceDumpCorrelator
{
    /// <summary>
    /// Evaluate all cross-source rules and return ranked findings.
    /// All parameters are nullable — missing data simply means that rule cannot fire.
    /// </summary>
    /// <param name="retainedByType">
    /// Optional map of type name → total retained bytes, computed from a pre-built BFS
    /// index.  When provided, Rule 1 uses retained sizes instead of shallow sizes for
    /// heap-dominance detection, giving a more accurate picture of how much memory each
    /// type actually holds (inclusive of all reachable objects).
    /// </param>
    public static IReadOnlyList<CorrelationFinding> Correlate(
        DumpSnapshot              snap,
        AllocTraceData?           alloc      = null,
        GcTraceData?              gc         = null,
        ContentionTraceData?      contention = null,
        ExceptionsTraceData?      exceptions = null,
        ThreadPoolStarvationData? starvation = null,
        HttpTraceData?            http       = null,
        AsyncTraceData?           async_     = null,
        SqlTraceData?             sql        = null,
        CpuTraceData?             cpu        = null,
        IReadOnlyDictionary<string, long>? retainedByType = null)
    {
        var findings = new List<CorrelationFinding>(10);

        CheckAllocConvergesWithHeapDominance(findings, alloc, snap, retainedByType);
        CheckAsyncBacklogConfirmsStarvation(findings, starvation, async_, snap);
        CheckExceptionFloodVsLiveExceptions(findings, exceptions, snap);
        CheckGcPauseVsLohFragmentation(findings, gc, snap);
        CheckContentionVsBlockedThreadCount(findings, contention, snap);
        CheckSlowSqlVsConnectionCount(findings, sql, snap);
        CheckPinnedHandlesAmplifyGcPause(findings, gc, snap);
        CheckFinalizerQueueBacklog(findings, alloc, snap);
        CheckHttpLatencyVsAsyncBacklog(findings, http, snap);
        CheckCpuSaturationVsThreadPool(findings, cpu, snap);

        findings.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return findings;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 1 — Allocation type from trace dominates the heap in the dump
    // Trace: top allocating type  ↔  Dump: same type is largest on heap
    // Confidence: very high — both sources point at same type
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckAllocConvergesWithHeapDominance(
        List<CorrelationFinding> findings,
        AllocTraceData?          alloc,
        DumpSnapshot             snap,
        IReadOnlyDictionary<string, long>? retainedByType)
    {
        if (alloc is null || alloc.TopTypes.Count == 0) return;
        if (snap.TopTypes.Count == 0) return;

        // Build a set of the top 5 allocating type names from the trace
        var traceTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int limit = Math.Min(5, alloc.TopTypes.Count);
        for (int i = 0; i < limit; i++)
            traceTopNames.Add(alloc.TopTypes[i].TypeName);

        // Find the first dump TopType that appears in the trace top allocators
        // TypeStat fields: Name, Count, TotalBytes
        foreach (var heapType in snap.TopTypes)
        {
            if (!traceTopNames.Contains(heapType.Name)) continue;

            // Prefer BFS-computed retained size when available; it captures all reachable
            // objects (not just the instances themselves), giving a stronger dominance signal.
            long shallowBytes = heapType.TotalBytes;
            bool hasRetained  = retainedByType?.TryGetValue(heapType.Name, out long retainedBytes) == true
                                && retainedBytes > shallowBytes;
            long effectiveBytes = hasRetained ? retainedByType![heapType.Name] : shallowBytes;

            // Determine how dominant this type is on the heap
            double heapSharePct = snap.TotalHeapBytes > 0
                ? effectiveBytes * 100.0 / snap.TotalHeapBytes
                : 0;

            if (heapSharePct < 5.0) break; // not dominant enough to be noteworthy

            // Find the trace-side alloc entry for numbers
            AllocTypeSummary? traceEntry = null;
            for (int i = 0; i < alloc.TopTypes.Count; i++)
                if (string.Equals(alloc.TopTypes[i].TypeName, heapType.Name, StringComparison.OrdinalIgnoreCase))
                { traceEntry = alloc.TopTypes[i]; break; }

            // Build a human-readable size label: show both shallow and retained when they differ.
            string sizeLabel = hasRetained
                ? $"{FormatSize(shallowBytes)} shallow / {FormatSize(effectiveBytes)} retained"
                : FormatSize(shallowBytes);

            int score = heapSharePct >= 30 ? 92 : heapSharePct >= 15 ? 80 : 68;
            findings.Add(new CorrelationFinding(
                Severity:         heapSharePct >= 30 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Category:         "Allocation / Heap",
                Headline:         $"Allocation hot type '{ShortName(heapType.Name)}' also dominates the heap ({heapSharePct:F1}% of heap bytes{(hasRetained ? ", retained" : "")})",
                Detail:
                    $"The trace shows '{heapType.Name}' as a top allocating type " +
                    (traceEntry is not null ? $"(~{FormatSize(traceEntry.EstimatedBytes)} sampled). " : ". ") +
                    $"The dump taken at the same time shows this type holds {sizeLabel} " +
                    $"({heapSharePct:F1}% of {FormatSize(snap.TotalHeapBytes)} total heap) " +
                    $"in {heapType.Count:N0} instances. " +
                    "Convergence of both signals is a strong indicator of accumulation — either a genuine leak " +
                    "or a cache growing without bound.",
                Advice:
                    $"1. Run 'memory-leak' and 'gc-roots' on the dump to find what is retaining these {heapType.Count:N0} instances.\n" +
                    $"2. Check whether '{ShortName(heapType.Name)}' implements IDisposable — if so, find disposal paths.\n" +
                    "3. Compare heap size across multiple dumps (use 'trend-analysis') to confirm the type is growing.",
                Score:            score,
                ContributingAreas:["alloc-trace", "dump"]));
            break; // one finding per rule
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 2 — Dump's async backlog confirms trace's thread pool starvation
    // Trace: starvation signal  ↔  Dump: AsyncBacklogTotal > threshold
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckAsyncBacklogConfirmsStarvation(
        List<CorrelationFinding> findings,
        ThreadPoolStarvationData? starvation,
        AsyncTraceData?           async_,
        DumpSnapshot              snap)
    {
        bool traceStarvation = starvation?.StarvationAdjustmentCount > 0
                            || async_?.SyncBlockingOccurrences > 0;
        if (!traceStarvation) return;
        if (snap.AsyncBacklogTotal < 20) return;

        int score = snap.AsyncBacklogTotal >= 200 ? 94 : snap.AsyncBacklogTotal >= 50 ? 82 : 70;
        string topMethod = snap.TopAsyncMethods.Count > 0
            ? $"The most common stuck state machine is '{snap.TopAsyncMethods[0].Name}' ({snap.TopAsyncMethods[0].Count}×)."
            : "";

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Critical,
            Category:         "Async / Threading",
            Headline:         $"Thread pool starvation (trace) confirmed by {snap.AsyncBacklogTotal:N0} stuck async state machines (dump)",
            Detail:
                $"The trace shows thread pool starvation — threads were being injected faster than work completed. " +
                $"The dump captured at the same time shows {snap.AsyncBacklogTotal:N0} async state machines still live on the heap, " +
                $"indicating work items were queued but not progressing. {topMethod} " +
                "This pattern is typically caused by sync-over-async code (blocking .Result or .Wait() on an async call " +
                "from a thread pool thread), which prevents threads from being returned to the pool.",
            Advice:
                "1. Run 'async-stacks' on the dump — look for async methods awaiting sync primitives.\n" +
                "2. Run 'thread-analysis' on the dump — identify threads blocked on Monitor.Wait, ManualResetEvent, etc.\n" +
                "3. Search the codebase for .Result, .Wait(), GetAwaiter().GetResult() called from async context.\n" +
                "4. Consider ConfigureAwait(false) throughout library code to prevent context deadlocks.",
            Score:            score,
            ContributingAreas:["thread-pool-starvation", "async-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 3 — Exception types from trace still live in the dump heap
    // Trace: exception storm  ↔  Dump: matching exception objects still alive
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckExceptionFloodVsLiveExceptions(
        List<CorrelationFinding> findings,
        ExceptionsTraceData?     exceptions,
        DumpSnapshot             snap)
    {
        if (exceptions is null || exceptions.TotalThrown < 100) return;
        if (snap.ExceptionCounts.Count == 0) return;

        // Build lookup of live exception types in dump
        var liveExTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nc in snap.ExceptionCounts)
            liveExTypes.Add(nc.Name);

        // Find trace exception types that are also alive in dump
        var overlap = new List<string>(4);
        foreach (var ex in exceptions.TopTypes)
        {
            if (liveExTypes.Contains(ex.ExceptionType) || liveExTypes.Contains(ex.ExceptionType + "Exception"))
                overlap.Add(ex.ExceptionType);
            if (overlap.Count >= 3) break;
        }

        if (overlap.Count == 0) return;

        string typesStr = string.Join(", ", overlap.Select(n => $"'{ShortName(n)}'"));
        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "Exceptions",
            Headline:         $"{exceptions.TotalThrown:N0} exceptions in trace — type(s) {typesStr} still live on heap in dump",
            Detail:
                $"The trace recorded {exceptions.TotalThrown:N0} thrown exceptions ({exceptions.UniqueTypes} unique types). " +
                $"The dump taken at the same time shows live instances of {typesStr} still on the managed heap. " +
                "This means exceptions are either being caught and stored (e.g. in a list, first-chance log, or AggregateException), " +
                "or they are not being collected because references are retained in static fields or thread-local state.",
            Advice:
                "1. Run 'exception-analysis' on the dump to see which code is holding live exception objects.\n" +
                "2. Run 'static-refs' on the dump — static exception collectors are a common retention path.\n" +
                $"3. In the trace, check the top frame for '{overlap[0]}' — it often reveals the throw site.",
            Score:            exceptions.TotalThrown >= 1000 ? 74 : 60,
            ContributingAreas:["exceptions-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 4 — GC pause pressure correlates with LOH fragmentation in dump
    // Trace: high GC avg pause  ↔  Dump: LOH fragmentation > 20%
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckGcPauseVsLohFragmentation(
        List<CorrelationFinding> findings,
        GcTraceData?             gc,
        DumpSnapshot             snap)
    {
        if (gc is null || gc.AvgPauseMs < 30.0) return;
        if (snap.LohFragmentationPct < 20.0 || snap.LohBytes < 10 * 1024 * 1024) return;

        int score = gc.AvgPauseMs >= 200 && snap.LohFragmentationPct >= 40 ? 85
                  : gc.AvgPauseMs >= 100 || snap.LohFragmentationPct >= 30  ? 72
                  : 58;

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "GC / Memory",
            Headline:         $"GC avg pause {gc.AvgPauseMs:F0} ms (trace) + LOH {snap.LohFragmentationPct:F1}% fragmented (dump) → large-object pressure",
            Detail:
                $"The trace shows an average GC pause of {gc.AvgPauseMs:F1} ms (max {gc.MaxPauseMs:F1} ms) across {gc.TotalGcs:N0} collections. " +
                $"The dump shows the Large Object Heap (LOH) is {snap.LohFragmentationPct:F1}% fragmented: " +
                $"{FormatSize(snap.LohLiveBytes)} live / {FormatSize(snap.LohBytes)} committed " +
                $"({FormatSize(snap.LohFreeBytes)} free holes). " +
                "The GCRuntime cannot compact the LOH by default (objects are too large to move without violating pinning semantics). " +
                "Free holes between live objects accumulate, and frequent allocations into a fragmented LOH trigger more Gen 2 GCs.",
            Advice:
                "1. Run 'heap-fragmentation' and 'large-objects' on the dump to identify the LOH culprits.\n" +
                "2. Consider object pooling (ArrayPool<T>, MemoryPool<T>) for large byte arrays.\n" +
                "3. Set GCSettings.LargeObjectHeapCompactionMode = CompactionMode.CompactOnce in critical scenarios.\n" +
                "4. Profile allocation sites in the trace ('alloc-trace') to find where LOH objects originate.",
            Score:            score,
            ContributingAreas:["gc-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 5 — Lock contention in trace confirmed by blocked threads in dump
    // Trace: high contention  ↔  Dump: many blocked threads
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckContentionVsBlockedThreadCount(
        List<CorrelationFinding> findings,
        ContentionTraceData?     contention,
        DumpSnapshot             snap)
    {
        if (contention is null || contention.TotalContentions < 50) return;
        if (snap.BlockedThreadCount < 5) return;

        double blockedPct = snap.AliveThreadCount > 0
            ? snap.BlockedThreadCount * 100.0 / snap.AliveThreadCount
            : 0;

        if (blockedPct < 20.0) return;

        int score = blockedPct >= 60 && contention.AvgWaitMs >= 100 ? 88
                  : blockedPct >= 40 || contention.TotalWaitMs >= 5000          ? 76
                  : 62;

        findings.Add(new CorrelationFinding(
            Severity:         blockedPct >= 60 ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category:         "Threading / Locks",
            Headline:         $"Lock contention ({contention.TotalContentions:N0} events, avg {contention.AvgWaitMs:F0} ms wait) + {snap.BlockedThreadCount}/{snap.AliveThreadCount} threads blocked — structural lock problem",
            Detail:
                $"The trace recorded {contention.TotalContentions:N0} lock contention events totaling {contention.TotalWaitMs:F0} ms of wait time " +
                $"(avg {contention.AvgWaitMs:F1} ms, max {contention.MaxWaitMs:F1} ms) across {contention.ThreadsAffected} threads. " +
                $"At dump time, {snap.BlockedThreadCount} of {snap.AliveThreadCount} live threads ({blockedPct:F0}%) are blocked. " +
                "The combination of sustained trace contention AND a high blocked-thread count in the dump indicates the lock pressure " +
                "is structural (a hot shared resource), not transient — threads are still waiting at the moment you captured the dump.",
            Advice:
                "1. Run 'deadlock-detection' on the dump — if threads are waiting on each other, there may be a deadlock.\n" +
                "2. Run 'thread-analysis' on the dump — inspect the stack of each blocked thread to identify the contested lock.\n" +
                $"3. In the trace ('contention-trace'), check the top hotspot frame: '{(contention.Hotspots.Count > 0 ? contention.Hotspots[0].Location : "unknown")}'.\n" +
                "4. Consider splitting the lock, using concurrent collections, or reducing critical section size.",
            Score:            score,
            ContributingAreas:["contention-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 6 — Slow SQL in trace + elevated live connection count in dump
    // Trace: slow commands  ↔  Dump: ConnectionCount suggests pool stress
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckSlowSqlVsConnectionCount(
        List<CorrelationFinding> findings,
        SqlTraceData?            sql,
        DumpSnapshot             snap)
    {
        if (sql is null || sql.SlowCommandCount < 3) return;
        if (snap.ConnectionCount < 10) return;

        int score = sql.SlowCommandCount >= 20 && snap.ConnectionCount >= 50 ? 80
                  : sql.SlowCommandCount >= 10 || snap.ConnectionCount >= 30   ? 68
                  : 55;

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "SQL / Data Access",
            Headline:         $"{sql.SlowCommandCount} slow SQL commands (trace) + {snap.ConnectionCount} live DB connections (dump) → possible connection pool pressure",
            Detail:
                $"The trace shows {sql.SlowCommandCount} SQL commands slower than {sql.SlowThresholdMs:F0} ms " +
                $"(max {sql.MaxCommandMs:F1} ms, avg {sql.AvgCommandMs:F1} ms). " +
                $"The dump captured at the same time has {snap.ConnectionCount} live database connection objects. " +
                "Slow queries hold connections for longer, preventing them from being returned to the pool. " +
                "If the pool limit is reached, new requests queue waiting for a connection — compounding the latency.",
            Advice:
                "1. Run 'connection-pool' on the dump to see how many connections are open, idle, or in use.\n" +
                "2. In the trace ('sql-trace'), review the slow query texts — are they missing indexes or fetching too many rows?\n" +
                "3. Ensure connections are disposed promptly (using statement or explicit Dispose).\n" +
                "4. Check connection pool max size: SqlConnectionStringBuilder.MaxPoolSize (default 100).",
            Score:            score,
            ContributingAreas:["sql-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 7 — Pinned handles in dump amplify GC pauses measured in trace
    // Trace: elevated GC pause  ↔  Dump: many pinned handles
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckPinnedHandlesAmplifyGcPause(
        List<CorrelationFinding> findings,
        GcTraceData?             gc,
        DumpSnapshot             snap)
    {
        if (gc is null || gc.AvgPauseMs < 20.0) return;
        if (snap.PinnedHandleCount < 20) return;

        int score = snap.PinnedHandleCount >= 500 ? 78 : snap.PinnedHandleCount >= 100 ? 65 : 52;

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "GC / Pinning",
            Headline:         $"{snap.PinnedHandleCount:N0} pinned handles (dump) may be inflating GC pauses (trace avg {gc.AvgPauseMs:F0} ms)",
            Detail:
                $"The dump shows {snap.PinnedHandleCount:N0} pinned GC handles. Pinned objects cannot be moved during compaction, " +
                $"which forces the GC to work around them and can fragment the SOH. " +
                $"The trace shows GC pauses averaging {gc.AvgPauseMs:F1} ms (max {gc.MaxPauseMs:F1} ms). " +
                "While pinning is sometimes necessary (e.g. for P/Invoke buffers), long-lived pins cause the collector to " +
                "leave holes in the heap, increasing the time needed to find free space for new allocations.",
            Advice:
                "1. Run 'pinned-objects' and 'handle-table' on the dump to identify which types are pinned and where.\n" +
                "2. Prefer Memory<T> / fixed() scoped pinning over GCHandle.Alloc(Pinned) for long-lived buffers.\n" +
                "3. Use MemoryPool<T> or ArrayPool<T> to avoid repeated small-array pinning.\n" +
                "4. In the trace ('gc-trace'), check if pauses correlate with LOH or SOH fragmentation events.",
            Score:            score,
            ContributingAreas:["gc-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 8 — High alloc rate in trace + deep finalizer queue in dump
    // Trace: allocation storm  ↔  Dump: FinalizerQueueDepth > threshold
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckFinalizerQueueBacklog(
        List<CorrelationFinding> findings,
        AllocTraceData?          alloc,
        DumpSnapshot             snap)
    {
        if (alloc is null || alloc.EstimatedTotalBytes < 50L * 1024 * 1024) return;
        if (snap.FinalizerQueueDepth < 50) return;

        int score = snap.FinalizerQueueDepth >= 500 ? 82 : snap.FinalizerQueueDepth >= 200 ? 70 : 58;
        string topFin = snap.TopFinalizerTypes.Count > 0
            ? $" Top finalizable type: '{ShortName(snap.TopFinalizerTypes[0].Name)}' ({snap.TopFinalizerTypes[0].Count}×)."
            : "";

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "GC / Finalizer",
            Headline:         $"High allocation rate (trace) + {snap.FinalizerQueueDepth:N0}-deep finalizer queue (dump) — finalizer thread falling behind",
            Detail:
                $"The trace shows ~{FormatSize(alloc.EstimatedTotalBytes)} of allocations sampled. " +
                $"The dump shows {snap.FinalizerQueueDepth:N0} objects waiting in the finalizer queue.{topFin} " +
                "The finalizer thread is single-threaded. When allocation rate is high and many objects have finalizers, " +
                "the queue grows faster than it can be drained. Objects in the finalizer queue survive one extra GC generation " +
                "before being freed, increasing memory pressure.",
            Advice:
                "1. Run 'finalizer-queue' on the dump for the full list of queued types.\n" +
                "2. Identify finalizable types that should implement IDisposable and be explicitly disposed.\n" +
                "3. For types that already implement IDisposable, ensure Dispose() is called before the object is abandoned.\n" +
                "4. Consider suppressing finalization after Dispose: GC.SuppressFinalize(this).",
            Score:            score,
            ContributingAreas:["alloc-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 9 — HTTP latency in trace correlates with async backlog in dump
    // Trace: slow HTTP requests  ↔  Dump: many stuck async state machines
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckHttpLatencyVsAsyncBacklog(
        List<CorrelationFinding> findings,
        HttpTraceData?           http,
        DumpSnapshot             snap)
    {
        if (http is null || http.AvgRequestMs < 200) return;
        if (snap.AsyncBacklogTotal < 30) return;

        int score = http.AvgRequestMs >= 1000 && snap.AsyncBacklogTotal >= 100 ? 79
                  : http.AvgRequestMs >= 500  || snap.AsyncBacklogTotal >= 50    ? 67
                  : 54;

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "HTTP / Async",
            Headline:         $"HTTP avg latency {http.AvgRequestMs:F0} ms (trace) + {snap.AsyncBacklogTotal:N0} stuck async state machines (dump) → requests waiting on async work",
            Detail:
                $"The trace shows HTTP requests with an average latency of {http.AvgRequestMs:F1} ms " +
                $"(max {http.MaxRequestMs:F1} ms, {http.TotalRequests:N0} requests total). " +
                $"The dump captured at the same time has {snap.AsyncBacklogTotal:N0} async state machines still live. " +
                "This combination suggests that HTTP request handlers are awaiting async operations that are delayed or stuck — " +
                "either due to sync-over-async blocking, downstream I/O latency, or an exhausted thread pool.",
            Advice:
                "1. Run 'async-stacks' on the dump to see which async methods are mid-flight and what they are awaiting.\n" +
                "2. Run 'http-trace' to identify the slowest URL paths — are they all hitting the same downstream service?\n" +
                "3. Check if slow HTTP requests correlate with slow SQL commands (compare 'sql-trace' timestamps).\n" +
                "4. Verify thread pool health: run 'thread-pool' on the dump to see available vs. active workers.",
            Score:            score,
            ContributingAreas:["http-trace", "dump"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 10 — High CPU in trace + low idle thread pool workers in dump
    // Trace: CPU saturation  ↔  Dump: thread pool near exhaustion
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckCpuSaturationVsThreadPool(
        List<CorrelationFinding> findings,
        CpuTraceData?            cpu,
        DumpSnapshot             snap)
    {
        // CpuTraceData exposes CPU% via Stats.AvgCpuPct
        double avgCpuPct = cpu?.Stats?.AvgCpuPct ?? 0;
        double maxCpuPct = cpu?.Stats?.MaxCpuPct ?? 0;
        if (cpu is null || avgCpuPct < 70.0) return;
        if (snap.TpMaxWorkers == 0) return;

        int idleWorkers = snap.TpIdleWorkers;
        double idlePct  = idleWorkers * 100.0 / snap.TpMaxWorkers;
        if (idlePct > 20.0) return; // still plenty of headroom

        int score = avgCpuPct >= 95 && idlePct < 5 ? 84
                  : avgCpuPct >= 85 || idlePct < 10  ? 71
                  : 58;

        findings.Add(new CorrelationFinding(
            Severity:         avgCpuPct >= 95 ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category:         "CPU / Thread Pool",
            Headline:         $"CPU at {avgCpuPct:F0}% avg (trace) + only {idleWorkers}/{snap.TpMaxWorkers} thread pool workers idle (dump) — near saturation",
            Detail:
                $"The trace shows the process consuming an average of {avgCpuPct:F1}% CPU (max {maxCpuPct:F1}%). " +
                $"The dump captured at the same time shows the thread pool has only {idleWorkers} idle workers " +
                $"out of {snap.TpMaxWorkers} maximum ({idlePct:F0}% idle). " +
                "Near-zero idle workers combined with high CPU means the pool is spending all its capacity on CPU work. " +
                "If any blocking I/O or locks are mixed in, new work items will queue and latency will spike.",
            Advice:
                "1. Run 'cpu-trace' to find the hot method — is it spinning, doing compute, or blocking?\n" +
                "2. Run 'thread-pool' on the dump for active vs. idle breakdown.\n" +
                "3. Move pure CPU work to a dedicated Task.Run or background queue to avoid starving I/O completion threads.\n" +
                "4. Check whether thread injection is throttled: Thread.Sleep(0) in a hot loop prevents injection.",
            Score:            score,
            ContributingAreas:["cpu-trace", "dump"]));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    // (helpers are defined below the rules — this is just a forward reference marker)

    private static string ShortName(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName[(dot + 1)..] : fullName;
    }

    private static string FormatSize(long bytes) => DumpHelpers.FormatSize(bytes);
}
