using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Cross-analyzer correlation engine.
/// Evaluates a fixed set of causal rules against the outputs of all eight trace
/// sub-analyzers and returns ranked <see cref="CorrelationFinding"/> records.
///
/// All inputs are nullable — the engine gracefully handles partial data (e.g. a trace
/// file with no HTTP events). Rules only fire when both contributing signals are present.
///
/// Findings are returned sorted by <see cref="CorrelationFinding.Score"/> descending.
/// </summary>
public static class CorrelationEngine
{
    /// <summary>
    /// Evaluate all correlation rules and return ranked findings.
    /// </summary>
    public static IReadOnlyList<CorrelationFinding> Correlate(
        CpuTraceData?             cpu,
        AllocTraceData?           alloc,
        GcTraceData?              gc,
        ContentionTraceData?      contention,
        ExceptionsTraceData?      exceptions,
        ThreadPoolStarvationData? starvation,
        JitTraceData?             jit,
        HttpTraceData?            http,
        AsyncTraceData?           async_ = null,
        SqlTraceData?             sql    = null)
    {
        var findings = new List<CorrelationFinding>(12);

        CheckAllocationStormDrivingGc(findings, alloc, gc);
        CheckLockContentionCausingStarvation(findings, contention, starvation);
        CheckExceptionStormCpuOverhead(findings, exceptions, cpu);
        CheckGcInducedHttpLatency(findings, gc, http);
        CheckSyncOverAsync(findings, starvation, contention);
        CheckJitColdStartHttpLatency(findings, jit, http);
        CheckLockSpinningCpuSaturation(findings, contention, cpu);
        CheckHighAllocAndHighGcPauseTime(findings, alloc, gc);
        CheckConfirmedSyncOverAsync(findings, starvation, async_);
        CheckSlowSqlInducingHttpLatency(findings, sql, http);
        CheckSqlErrorsExceptionStorm(findings, sql, exceptions);

        findings.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return findings;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 1 — High allocation rate driving frequent GC
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckAllocationStormDrivingGc(
        List<CorrelationFinding> findings,
        AllocTraceData?          alloc,
        GcTraceData?             gc)
    {
        if (alloc is null || gc is null) return;
        if (alloc.EstimatedTotalBytes < 200L * 1024 * 1024) return;  // < 200 MB estimated — not interesting
        if (gc.TotalGcs < 10) return;
        if (gc.AvgPauseMs < 5) return;

        // Score: higher when both allocation and GC activity are large
        bool highAlloc  = alloc.EstimatedTotalBytes > 1L * 1024 * 1024 * 1024;
        bool highGcPause = gc.MaxPauseMs >= 200;
        bool manyGcs    = gc.TotalGcs >= 50;

        int score = 50;
        if (highAlloc)  score += 20;
        if (highGcPause) score += 20;
        if (manyGcs)    score += 10;
        score = Math.Min(score, 95);

        string topType = alloc.TopTypes.Count > 0
            ? $" Top type: {TrimType(alloc.TopTypes[0].TypeName)}." : "";

        findings.Add(new CorrelationFinding(
            Severity:         score >= 75 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:         "GC / Allocation",
            Headline:         "High allocation rate is driving GC pressure",
            Detail:           $"~{FormatBytes(alloc.EstimatedTotalBytes)} allocated from {alloc.TotalTicks:N0} ticks, " +
                              $"causing {gc.TotalGcs:N0} GC events (max pause {gc.MaxPauseMs:F0} ms, avg {gc.AvgPauseMs:F1} ms).{topType}",
            Advice:           "Reduce per-request allocation by reusing buffers (ArrayPool<T>), caching results, " +
                              "or using struct/value types in hot paths. See the Allocation Trace chapter for the top call sites.",
            Score:            score,
            ContributingAreas: ["alloc-trace", "gc-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 2 — Lock contention contributing to ThreadPool starvation
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckLockContentionCausingStarvation(
        List<CorrelationFinding> findings,
        ContentionTraceData?     contention,
        ThreadPoolStarvationData? starvation)
    {
        if (contention is null || starvation is null) return;
        if (contention.TotalContentions < 50) return;
        if (starvation.StarvationAdjustmentCount < 1) return;

        int score = 60;
        if (contention.TotalWaitMs > 5_000)  score += 15;
        if (starvation.StarvationAdjustmentCount >= 5) score += 15;
        score = Math.Min(score, 95);

        string hotspot = contention.Hotspots.Count > 0
            ? $" Hottest lock site: {TrimFrame(contention.Hotspots[0].Location)}." : "";

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "Threading",
            Headline:         "Lock contention is contributing to ThreadPool starvation",
            Detail:           $"{contention.TotalContentions:N0} lock contentions ({contention.TotalWaitMs:F0} ms total wait) " +
                              $"combined with {starvation.StarvationAdjustmentCount} starvation adjustment(s) indicates ThreadPool " +
                              $"workers are blocking on locks.{hotspot}",
            Advice:           "Replace synchronous lock patterns inside async code with SemaphoreSlim or Channel<T>. " +
                              "Avoid holding Monitor locks across await points. " +
                              "See the Contention and Thread Pool Starvation chapters.",
            Score:            score,
            ContributingAreas: ["contention-trace", "thread-pool-starvation"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 3 — Exception storm inflating CPU usage
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckExceptionStormCpuOverhead(
        List<CorrelationFinding> findings,
        ExceptionsTraceData?     exceptions,
        CpuTraceData?            cpu)
    {
        if (exceptions is null || cpu is null) return;
        if (exceptions.TotalThrown < 500) return;
        if (cpu.Stats is not { AvgCpuPct: >= 30 }) return;

        int score = 55;
        if (exceptions.TotalThrown >= 10_000) score += 25;
        else if (exceptions.TotalThrown >= 2_000) score += 15;
        if (cpu.Stats!.AvgCpuPct >= 70) score += 15;
        score = Math.Min(score, 95);

        string topEx = exceptions.TopTypes.Count > 0
            ? $" Most frequent: {exceptions.TopTypes[0].ExceptionType} ({exceptions.TopTypes[0].Count:N0}×)." : "";

        findings.Add(new CorrelationFinding(
            Severity:         score >= 75 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:         "Exceptions / CPU",
            Headline:         "Exception storm is contributing to CPU overhead",
            Detail:           $"{exceptions.TotalThrown:N0} exceptions thrown (avg CPU {cpu.Stats!.AvgCpuPct:F1}%). " +
                              $"Each exception captures a full stack trace, which is expensive at high rates.{topEx}",
            Advice:           "Replace exception-as-control-flow patterns with TryXxx methods or Result<T> types. " +
                              "See the Exceptions Trace chapter for the top throw sites.",
            Score:            score,
            ContributingAreas: ["exceptions-trace", "cpu-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 4 — GC pauses causing HTTP latency spikes
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckGcInducedHttpLatency(
        List<CorrelationFinding> findings,
        GcTraceData?             gc,
        HttpTraceData?           http)
    {
        if (gc is null || http is null) return;
        if (!http.HasData || http.TotalRequests < 5) return;
        if (gc.MaxPauseMs < 100) return;
        if (http.P99RequestMs < 500) return;

        // Correlation strength: if max GC pause is a significant fraction of P99 latency
        double ratio = gc.MaxPauseMs / Math.Max(1, http.P99RequestMs);

        int score = 55;
        if (ratio >= 0.5)  score += 25;
        else if (ratio >= 0.2) score += 15;
        if (gc.MaxPauseMs >= 500) score += 10;
        score = Math.Min(score, 95);

        findings.Add(new CorrelationFinding(
            Severity:         score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:         "GC / HTTP",
            Headline:         "GC pauses are likely inflating HTTP request latency",
            Detail:           $"HTTP P99 latency is {http.P99RequestMs:F0} ms and max GC pause is {gc.MaxPauseMs:F0} ms " +
                              $"({ratio * 100:F0}% of P99). Blocking GC pauses stall all threads, including request-processing threads.",
            Advice:           "Reduce heap pressure to avoid long Gen2 and LOH pauses. " +
                              "Check the GC Trace chapter for trigger reasons (Induced, AllocLarge, OutOfSpaceLOH). " +
                              "Profile allocation hot spots in the Allocation Trace chapter.",
            Score:            score,
            ContributingAreas: ["gc-trace", "http-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 5 — Synchronous blocking calls on ThreadPool threads (sync-over-async)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckSyncOverAsync(
        List<CorrelationFinding> findings,
        ThreadPoolStarvationData? starvation,
        ContentionTraceData?      contention)
    {
        if (starvation is null) return;
        if (starvation.StarvationAdjustmentCount < 1) return;

        // Look for MonitorWait or WaitOne in wait events — classic sync-over-async
        bool hasBlockingWaits = false;
        int  blockingWaitCount = 0;
        foreach (var we in starvation.WaitEvents)
        {
            if (we.WaitSourceName is "MonitorWait" or "WaitOne" or "WaitAll" or "WaitAny")
            {
                hasBlockingWaits = true;
                blockingWaitCount++;
            }
        }

        if (!hasBlockingWaits && contention is null) return;

        int score = 65;
        if (starvation.StarvationAdjustmentCount >= 5) score += 15;
        if (blockingWaitCount >= 3) score += 10;
        score = Math.Min(score, 95);

        string blockInfo = blockingWaitCount > 0
            ? $" {blockingWaitCount} thread(s) performing synchronous wait-handle blocks (MonitorWait/WaitOne)." : "";

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "Threading",
            Headline:         "Synchronous blocking on ThreadPool threads detected (sync-over-async)",
            Detail:           $"{starvation.StarvationAdjustmentCount} ThreadPool starvation adjustment(s) detected.{blockInfo} " +
                              $"Blocking waits on pool threads prevent work dispatch and trigger injection of additional threads.",
            Advice:           "Identify .Result, .Wait(), and Thread.Sleep() calls inside async code paths. " +
                              "Replace with await and async-compatible primitives. " +
                              "See the Thread Pool Starvation chapter for the blocking thread list.",
            Score:            score,
            ContributingAreas: ["thread-pool-starvation", "contention-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 6 — JIT cold-start contributing to high initial HTTP latency
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckJitColdStartHttpLatency(
        List<CorrelationFinding> findings,
        JitTraceData?            jit,
        HttpTraceData?           http)
    {
        if (jit is null || http is null) return;
        if (!http.HasData || http.TotalRequests < 3) return;
        if (jit.TotalJitTimeMs < 1_000) return;
        if (http.P99RequestMs < 1_000) return;
        if (!jit.TimingAvailable) return;

        int score = 50;
        if (jit.TotalJitTimeMs >= 5_000)  score += 20;
        if (http.P99RequestMs >= 3_000)   score += 15;
        if (jit.TotalMethodsJitted >= 5_000) score += 10;
        score = Math.Min(score, 85);  // capped lower — correlation is indirect

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Info,
            Category:         "JIT / HTTP",
            Headline:         "JIT warm-up cost may be contributing to elevated HTTP latency",
            Detail:           $"Total JIT time: {jit.TotalJitTimeMs:F0} ms for {jit.TotalMethodsJitted:N0} methods. " +
                              $"HTTP P99 latency: {http.P99RequestMs:F0} ms. " +
                              $"If elevated latency occurs at startup or after app pool recycles, JIT compilation of cold paths is a likely factor.",
            Advice:           "Publish with ReadyToRun (dotnet publish -r <rid>) to pre-compile hot assemblies. " +
                              "Consider a warm-up probe endpoint that exercises the critical code paths before traffic arrives. " +
                              "See the JIT Trace chapter for the slowest methods to compile.",
            Score:            score,
            ContributingAreas: ["jit-trace", "http-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 7 — Lock contention contributing to CPU saturation (lock spinning)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckLockSpinningCpuSaturation(
        List<CorrelationFinding> findings,
        ContentionTraceData?     contention,
        CpuTraceData?            cpu)
    {
        if (contention is null || cpu is null) return;
        if (contention.TotalContentions < 100) return;
        if (cpu.Stats is not { AvgCpuPct: >= 50 }) return;

        // Look for Monitor.Enter or JIT_MonEnter in top CPU methods
        bool hasMonitorInCpu = false;
        foreach (var m in cpu.TopMethods)
        {
            if (m.Method.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
                m.Method.Contains("JIT_MonEnter", StringComparison.OrdinalIgnoreCase) ||
                m.Method.Contains("MonitorEnter", StringComparison.OrdinalIgnoreCase))
            {
                hasMonitorInCpu = true;
                break;
            }
        }

        // Still worth reporting if contention is very high even without explicit Monitor in CPU top-N
        bool highContention = contention.TotalContentions >= 500 || contention.TotalWaitMs >= 10_000;
        if (!hasMonitorInCpu && !highContention) return;

        int score = 55;
        if (hasMonitorInCpu) score += 20;
        if (contention.TotalWaitMs >= 10_000) score += 15;
        if (cpu.Stats!.AvgCpuPct >= 75) score += 10;
        score = Math.Min(score, 90);

        string monitorNote = hasMonitorInCpu
            ? " Monitor.Enter appears in the CPU top-methods list — lock spinning is inflating CPU usage." : "";

        findings.Add(new CorrelationFinding(
            Severity:         score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:         "Contention / CPU",
            Headline:         "Lock contention is contributing to CPU saturation",
            Detail:           $"{contention.TotalContentions:N0} lock contentions ({contention.TotalWaitMs:F0} ms total wait) " +
                              $"with avg CPU {cpu.Stats!.AvgCpuPct:F1}%.{monitorNote}",
            Advice:           "Check the Contention chapter for the hot lock site. " +
                              "Consider replacing fine-grained locks with ConcurrentDictionary, lock-free algorithms, " +
                              "or partitioned data structures to reduce contention and CPU waste.",
            Score:            score,
            ContributingAreas: ["contention-trace", "cpu-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 8 — Very high allocation + high total GC pause time (allocation tsunami)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckHighAllocAndHighGcPauseTime(
        List<CorrelationFinding> findings,
        AllocTraceData?          alloc,
        GcTraceData?             gc)
    {
        if (alloc is null || gc is null) return;
        // Only fire if Rule 1 would NOT fire (avoid duplication) — i.e. GC is heavy but GC count is low (large pauses)
        if (alloc.EstimatedTotalBytes < 500L * 1024 * 1024) return;
        if (gc.TotalPauseMs < 2_000) return;
        if (gc.TotalGcs >= 10) return;  // Rule 1 covers this case

        int score = 60;
        if (gc.TotalPauseMs >= 10_000) score += 20;
        if (alloc.EstimatedTotalBytes > 2L * 1024 * 1024 * 1024) score += 15;
        score = Math.Min(score, 90);

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "GC / Allocation",
            Headline:         "Large allocations are causing significant GC pause time",
            Detail:           $"~{FormatBytes(alloc.EstimatedTotalBytes)} estimated allocation with {gc.TotalPauseMs:F0} ms total GC pause " +
                              $"across {gc.TotalGcs} GC event(s). Large single allocations promote objects to Gen2/LOH early, " +
                              $"triggering expensive full-heap collections.",
            Advice:           "Identify large object allocations (> 85 KB) in the Allocation Trace chapter. " +
                              "Use ArrayPool<T> or MemoryPool<T> for large buffers. " +
                              "Review LOH trigger reasons in the GC Trace chapter.",
            Score:            score,
            ContributingAreas: ["alloc-trace", "gc-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1 << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1 << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1 << 10):F1} KB";
        return $"{bytes} B";
    }

    private static string TrimType(string name) =>
        name.Length <= 50 ? name : "…" + name[^49..];

    private static string TrimFrame(string frame) =>
        frame.Length <= 80 ? frame : "…" + frame[^79..];

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 9 — Confirmed sync-over-async (starvation + blocking task hotspots)
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckConfirmedSyncOverAsync(
        List<CorrelationFinding> findings,
        ThreadPoolStarvationData? starvation,
        AsyncTraceData?           async_)
    {
        if (starvation is null || async_ is null) return;
        if (!async_.HasData) return;
        if (starvation.StarvationAdjustmentCount < 1) return;
        if (async_.SyncBlockingOccurrences < 1) return;

        int score = 70;
        if (starvation.StarvationAdjustmentCount >= 5) score += 10;
        if (async_.SyncBlockingOccurrences >= 10)      score += 10;
        score = Math.Min(score, 95);

        string hotspot = async_.SyncBlockingHotspots.Count > 0
            ? $" Top blocking site: {async_.SyncBlockingHotspots[0].Frame}" : "";

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "Async / Threading",
            Headline:         "Confirmed sync-over-async: Task.Wait/.Result blocks detected with ThreadPool starvation",
            Detail:           $"{starvation.StarvationAdjustmentCount} starvation adjustment(s) and " +
                              $"{async_.SyncBlockingOccurrences} synchronous Task-block(s) detected.{hotspot} " +
                              $"Synchronous blocking on async continuations deadlocks the ThreadPool under high concurrency.",
            Advice:           "Replace all .Result, .Wait(), GetAwaiter().GetResult() inside async contexts with await. " +
                              "Review the Async / Task Trace chapter for specific blocking call sites.",
            Score:            score,
            ContributingAreas: ["thread-pool-starvation", "async-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 10 — Slow SQL commands inducing HTTP latency
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckSlowSqlInducingHttpLatency(
        List<CorrelationFinding> findings,
        SqlTraceData?            sql,
        HttpTraceData?           http)
    {
        if (sql is null || http is null) return;
        if (!sql.HasData || !http.HasData) return;
        if (sql.MaxCommandMs < 1_000) return;
        if (http.P99RequestMs < 1_000) return;

        int score = 55;
        if (sql.MaxCommandMs >= 5_000)  score += 20;
        if (http.P99RequestMs >= 3_000) score += 15;
        if (sql.SlowCommandCount >= 10) score += 10;
        score = Math.Min(score, 90);

        findings.Add(new CorrelationFinding(
            Severity:         score >= 70 ? FindingSeverity.Warning : FindingSeverity.Info,
            Category:         "SQL / HTTP",
            Headline:         "Slow database queries are likely contributing to elevated HTTP response times",
            Detail:           $"Slowest SQL command: {sql.MaxCommandMs:F0} ms. " +
                              $"HTTP P99 latency: {http.P99RequestMs:F0} ms. " +
                              $"{sql.SlowCommandCount} SQL command(s) exceeded the slow threshold ({sql.SlowThresholdMs} ms).",
            Advice:           "Review the SQL / EF Trace chapter for the slowest queries. " +
                              "Add appropriate indexes, check for N+1 query patterns, " +
                              "and verify connection pool sizing in the Connection Pool report.",
            Score:            score,
            ContributingAreas: ["sql-trace", "http-trace"]));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 11 — SQL errors generating exception storm
    // ─────────────────────────────────────────────────────────────────────────
    private static void CheckSqlErrorsExceptionStorm(
        List<CorrelationFinding> findings,
        SqlTraceData?            sql,
        ExceptionsTraceData?     exceptions)
    {
        if (sql is null || exceptions is null) return;
        if (!sql.HasData) return;
        if (sql.TotalErrors < 10) return;
        if (exceptions.TotalThrown < 1_000) return;

        int score = 55;
        if (sql.TotalErrors >= 100)           score += 15;
        if (exceptions.TotalThrown >= 10_000) score += 15;
        score = Math.Min(score, 85);

        findings.Add(new CorrelationFinding(
            Severity:         FindingSeverity.Warning,
            Category:         "SQL / Exceptions",
            Headline:         "SQL errors may be driving the exception flood",
            Detail:           $"{sql.TotalErrors:N0} SQL error(s) recorded alongside " +
                              $"{exceptions.TotalThrown:N0} total exception(s). " +
                              $"SqlException and related types are often thrown for each failed command, " +
                              $"amplifying exception count when a database or connection is unhealthy.",
            Advice:           "Correlate exception types in the Exceptions chapter with SqlException. " +
                              "Investigate the root cause of SQL failures (connection string, timeouts, deadlocks). " +
                              "Add retry logic with exponential back-off for transient failures.",
            Score:            score,
            ContributingAreas: ["sql-trace", "exceptions-trace"]));
    }
}
