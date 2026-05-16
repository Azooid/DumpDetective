using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Synthesizes ranked causal chains from all other trace analyzer outputs and
/// CorrelationEngine findings. No new ETW event loop is required.
/// Each causal chain identifies a root cause, downstream effects, and actionable advice.
/// </summary>
public sealed class RootCauseChainAnalyzer
{
    /// <summary>
    /// Builds ranked causal chains from previously computed analyzer results.
    /// All arguments except traceFileName are optional (nullable).
    /// </summary>
    public RootCauseChainData Analyze(
        string traceFileName,
        IReadOnlyList<CorrelationFinding>? correlations   = null,
        CpuTraceData?                      cpu            = null,
        AllocTraceData?                    alloc          = null,
        GcTraceData?                       gc             = null,
        ContentionTraceData?               contention     = null,
        ExceptionsTraceData?               exceptions     = null,
        ThreadPoolStarvationData?          starvation     = null,
        JitTraceData?                      jit            = null,
        HttpTraceData?                     http           = null,
        AsyncTraceData?                    async_         = null,
        SqlTraceData?                      sql            = null,
        FinalizerTraceData?                finalizer      = null,
        ConnectionPoolTraceData?           connPool       = null,
        AllocationBurstData?               allocBurst     = null,
        DeadlockPatternData?               deadlock       = null,
        LohTraceData?                      loh            = null,
        RetryStormData?                    retryStorm     = null,
        TaskSchedulerTraceData?            taskScheduler  = null,
        FileIoTraceData?                   fileIo         = null,
        SocketTraceData?                   socket         = null,
        DnsTraceData?                      dns            = null,
        AnomalyDetectionData?              anomaly        = null)
    {
        var chains = new List<CausalChain>();

        // ── Rule: High GC pause + high allocation → memory pressure root cause ──
        if (gc is { MaxPauseMs: > 200 } && alloc is not null)
        {
            var effects = new List<string> { "Increased GC pause time (max " + gc.MaxPauseMs.ToString("F0") + " ms)" };
            var evidence = new List<string> { $"GC: {gc.TotalGcs} collections, max pause {gc.MaxPauseMs:F0} ms" };
            if (alloc.EstimatedTotalBytes > 0)
            {
                effects.Add($"Allocation: ~{alloc.EstimatedTotalBytes / 1024:N0} KB (sampled)");
                evidence.Add($"Allocation ticks: {alloc.TotalTicks} (~{alloc.EstimatedTotalBytes / 1024:N0} KB)");
            }
            if (http is { SlowRequestCount: > 0 })
            {
                effects.Add($"{http.SlowRequestCount} slow HTTP requests");
                evidence.Add($"HTTP: {http.SlowRequestCount} requests exceeded latency threshold");
            }
            chains.Add(new CausalChain(
                gc.MaxPauseMs > 500 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Score: gc.MaxPauseMs > 500 ? 90 : 70,
                RootCause: "High allocation rate driving excessive GC pressure",
                Effects: effects,
                Evidence: evidence,
                Advice: "Profile allocation hotspots (dotnet-trace with GCAllocationTick). " +
                        "Consider object pooling, struct types for short-lived data, and reducing Gen2 promotion.",
                ContributingAreas: ["GC", "Memory", "Allocation"]));
        }

        // ── Rule: Deadlock or long waits → thread starvation ──
        if (deadlock is { SuspectedDeadlockCount: > 0 })
        {
            var effects = new List<string>
            {
                $"{deadlock.SuspectedDeadlockCount} suspected deadlock pattern(s)",
                $"{deadlock.LongWaitCount} threads with long wait time (max {deadlock.MaxWaitMs:F0} ms)"
            };
            if (starvation is { StarvationAdjustmentCount: > 0 })
                effects.Add($"Thread pool starvation: {starvation.StarvationAdjustmentCount} adjustment(s)");

            chains.Add(new CausalChain(
                FindingSeverity.Critical,
                Score: 95,
                RootCause: "Circular or prolonged lock acquisition pattern (suspected deadlock)",
                Effects: effects,
                Evidence: deadlock.WaitChains.Take(3).Select(w =>
                    $"Thread {w.Thread1Id} ↔ Thread {w.Thread2Id}: {w.OverlapMs:F0} ms overlap " +
                    $"at [{w.Thread1Frame}] / [{w.Thread2Frame}]").ToList(),
                Advice: "Use 'deadlock-detection' dump analysis to confirm lock ownership. " +
                        "Standardize lock acquisition order. Prefer async/await over synchronous blocking.",
                ContributingAreas: ["Threading", "Deadlock", "Contention"]));
        }

        // ── Rule: Retry storm + exceptions ──
        if (retryStorm is { BurstCount: > 0 })
        {
            var effects = new List<string> { $"{retryStorm.TotalRetryExceptions} retry-pattern exceptions" };
            if (exceptions is not null)
                effects.Add($"{exceptions.TotalThrown} total exceptions in trace");
            if (http is not null)
                effects.Add($"HTTP tail latency likely elevated");

            chains.Add(new CausalChain(
                retryStorm.BurstCount >= 3 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Score: 80,
                RootCause: "Transient dependency failure triggering retry storm",
                Effects: effects,
                Evidence: retryStorm.AffectedExceptionTypes.Take(5)
                    .Select(t => $"Exception: {t}").ToList(),
                Advice: "Add jitter to retry policies. Use circuit breaker pattern. " +
                        "Check downstream service health and timeout configuration.",
                ContributingAreas: ["Network", "Exceptions", "Resilience"]));
        }

        // ── Rule: LOH growth + GC pauses ──
        if (loh is { IsTrendingUp: true })
        {
            chains.Add(new CausalChain(
                FindingSeverity.Warning,
                Score: 65,
                RootCause: "LOH fragmentation from growing large object allocations",
                Effects:
                [
                    $"LOH grew {loh.LohGrowthBytes / 1024 / 1024:N0} MB during trace",
                    "Increased Gen2/Full GC frequency"
                ],
                Evidence:
                [
                    $"Start: {loh.StartLohBytes / 1024 / 1024:N0} MB → Peak: {loh.PeakLohBytes / 1024 / 1024:N0} MB"
                ],
                Advice: "Audit allocations > 85 KB. Use ArrayPool<T>/MemoryPool<T>. " +
                        "Enable Server GC with LOH compaction (GCSettings.LargeObjectHeapCompactionMode).",
                ContributingAreas: ["GC", "LOH", "Memory"]));
        }

        // ── Rule: DB connection pool issues ──
        if (connPool is { LeakedConnections: > 0 })
        {
            chains.Add(new CausalChain(
                FindingSeverity.Critical,
                Score: 88,
                RootCause: "Database connection pool exhaustion from unclosed connections",
                Effects:
                [
                    $"{connPool.LeakedConnections} connections opened without corresponding close",
                    "New requests may block waiting for pool connections"
                ],
                Evidence: connPool.TopDatabases.Take(3)
                    .Select(d => $"{d.Database}: {d.Opens} opens, {d.Closes} closes").ToList(),
                Advice: "Ensure SqlConnection (and DbConnection) is always disposed in a using block. " +
                        "Check max pool size (default 100). Monitor pool wait timeouts.",
                ContributingAreas: ["Database", "Connections", "Resource Leak"]));
        }

        // ── Rule: DNS failures + socket failures → network instability ──
        if (dns is { TotalFailed: > 5 } && socket is { TotalConnectFailed: > 5 })
        {
            chains.Add(new CausalChain(
                FindingSeverity.Warning,
                Score: 72,
                RootCause: "Network instability: DNS resolution failures compounding socket connection failures",
                Effects:
                [
                    $"{dns.TotalFailed} DNS resolutions failed",
                    $"{socket.TotalConnectFailed} socket connect failures"
                ],
                Evidence:
                [
                    $"DNS: {dns.TotalResolutions} queries, {dns.TotalFailed} failed (max {dns.MaxResolutionMs:F0} ms)",
                    $"Sockets: {socket.TotalConnects} connects, {socket.TotalConnectFailed} failed"
                ],
                Advice: "Check network path to dependency endpoints. Review DNS caching (TTL). " +
                        "Add circuit breakers for network-dependent operations.",
                ContributingAreas: ["Network", "DNS", "Socket"]));
        }

        // ── Incorporate CorrelationEngine findings as chains ──
        if (correlations is not null)
        {
            foreach (var cf in correlations)
            {
                var evidence = new List<string>(2)
                {
                    $"Confidence: {cf.ConfidenceLabel} ({cf.Score}/100)",
                };
                if (cf.ContributingAreas.Length > 0)
                    evidence.Add($"Sources: {string.Join(", ", cf.ContributingAreas)}");

                chains.Add(new CausalChain(
                    cf.Severity,
                    Score: cf.Score,
                    RootCause: cf.Headline,
                    Effects: [cf.Detail],
                    Evidence: evidence,
                    Advice: cf.Advice,
                    ContributingAreas: [cf.Category]));
            }
        }

        // ── Incorporate high-severity anomalies ──
        if (anomaly is not null)
        {
            foreach (var a in anomaly.Anomalies.Where(a => a.Severity >= FindingSeverity.Warning))
            {
                chains.Add(new CausalChain(
                    a.Severity,
                    Score: (int)(a.ZScore * 10),
                    RootCause: $"Statistical anomaly: {a.MetricName}",
                    Effects: [a.Description],
                    Evidence: [$"Observed {a.ObservedValue:F1}, baseline {a.BaselineValue:F1} (z={a.ZScore:F1})"],
                    Advice: "Investigate the metric spike at the indicated time offset. " +
                            "Correlate with deployment events, traffic spikes, or dependency changes.",
                    ContributingAreas: ["Anomaly"]));
            }
        }

        chains.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        if (chains.Count == 0)
        {
            return new RootCauseChainData(
                $"{traceFileName}  |  No dominant root-cause chains identified",
                TotalChains: 0,
                TopSeverity: FindingSeverity.Info,
                CausalChains: [],
                HasData: true);
        }

        var topSeverity = chains.Max(c => c.Severity);
        string info = $"{traceFileName}  |  {chains.Count} causal chain(s)  •  top severity: {topSeverity}";

        return new RootCauseChainData(info, chains.Count, topSeverity, chains, HasData: true);
    }
}
