using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Cross-signal correlation for a single dump snapshot (no trace file involved).
/// Mirrors the pattern in <c>DumpDetective.Analysis.Trace.CorrelationEngine</c> — a
/// fixed set of rules over already-computed data, each producing a scored
/// <see cref="CorrelationFinding"/> when two or more independent signals agree that
/// they likely share one root cause.
///
/// <see cref="HealthScorer"/> answers "what is wrong, one signal at a time". This
/// answers "which of those signals are actually the same underlying problem" — the
/// same distinction <c>trace-dump-analyze</c> already draws between a single
/// sub-analyzer's findings and <c>TraceDumpCorrelator</c>'s cross-source findings.
///
/// Pure POCO in/out (<see cref="DumpSnapshot"/> + <see cref="ScoringThresholds"/>) —
/// no ClrMD types, unit-testable without a dump, and reuses the exact thresholds
/// <see cref="HealthScorer"/> already scores against so a correlation never fires
/// on a signal that wouldn't also produce its own single-signal Finding.
/// </summary>
public static class DumpCorrelationEngine
{
    public static IReadOnlyList<CorrelationFinding> Correlate(DumpSnapshot s, ScoringThresholds t)
    {
        var findings = new List<CorrelationFinding>(11);

        double gen2Pct = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;

        // Named, specific pairs first — each one makes a concrete causal claim.
        CheckGen2RetentionWithFinalizerBacklog(findings, s, t, gen2Pct);
        CheckEventLeakDrivingRetention(findings, s, t, gen2Pct);
        CheckThreadPoolSaturationWithAsyncBacklog(findings, s, t);
        CheckFragmentationWithPinning(findings, s, t);
        CheckMultiResourceLeakConvergence(findings, s, t);
        CheckLargeHeapDrivenByGen2(findings, s, t, gen2Pct);
        CheckLohGrowthWithFragmentation(findings, s, t);
        CheckExceptionStormWithHeapPressure(findings, s, t, gen2Pct);
        CheckConnectionExhaustionWithWcfFaults(findings, s, t);

        // Generic structural safety net last — fires whenever the specific rules above
        // don't cover the exact combination present, so this section is never empty on
        // a dump that's genuinely unhealthy across multiple domains. Deliberately scored
        // lower than the named rules: it's a broader, softer signal, not a causal claim.
        CheckBroadDomainConvergence(findings, s);

        findings.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return findings;
    }

    // ── Rule 1 — Gen2 retention + a growing finalizer backlog ──────────────────
    private static void CheckGen2RetentionWithFinalizerBacklog(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t, double gen2Pct)
    {
        if (gen2Pct < t.Gen2WarnPct || s.FinalizerQueueDepth <= t.FinalizerWarn) return;

        bool critTier = gen2Pct >= t.Gen2CritPct && s.FinalizerQueueDepth > t.FinalizerCrit;
        int score = 65 + (critTier ? 25 : 0) + (gen2Pct >= t.Gen2CritPct ? 5 : 0) + (s.FinalizerQueueDepth > t.FinalizerCrit ? 5 : 0);

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Memory / GC",
            Headline: "Gen2 retention compounded by a growing finalizer queue",
            Detail: $"Gen2 holds {gen2Pct:F0}% of the managed heap ({DumpHelpers.FormatSize(s.Gen2Bytes)}) while " +
                    $"{s.FinalizerQueueDepth:N0} objects sit in the finalizer queue. Objects awaiting finalization " +
                    "delay collection of everything they retain, which reinforces Gen2 growth rather than just " +
                    "coinciding with it.",
            Advice: "Run 'memory-leak <dump>' for GC root chains, then 'finalizer-queue <dump>' to identify which " +
                    "types are missing Dispose()/using — clearing the backlog often relieves the Gen2 pressure too.",
            Score: Math.Min(score, 95),
            ContributingAreas: ["memory-leak", "finalizer-queue"]));
    }

    // ── Rule 2 — Event handler leak driving Gen2 growth ────────────────────────
    private static void CheckEventLeakDrivingRetention(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t, double gen2Pct)
    {
        if (s.EventSubscriberTotal <= t.EventTotalWarn || gen2Pct < t.Gen2WarnPct) return;

        bool critTier = s.EventLeakMaxOnField > t.EventPerFieldCrit && gen2Pct >= t.Gen2CritPct;
        int score = 60 + (critTier ? 25 : 0) + (s.EventLeakMaxOnField > t.EventPerFieldCrit ? 10 : 0);

        string? topLeak = s.TopEventLeaks.FirstOrDefault() is { } el
            ? $"{el.PublisherType}.{el.FieldName} ({el.Subscribers:N0} subscribers)" : null;

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Leaks / Memory",
            Headline: "Event handler leak is likely driving Gen2 growth",
            Detail: $"{s.EventLeakFieldCount:N0} event field(s) hold {s.EventSubscriberTotal:N0} subscribers total " +
                    $"while Gen2 holds {gen2Pct:F0}% of the heap.{(topLeak is not null ? $" Largest: {topLeak}." : "")} " +
                    "Each subscriber keeps its publisher — and everything the publisher retains — alive for as " +
                    "long as the subscription exists.",
            Advice: "Run 'event-analysis <dump>' to find the publisher field(s), then confirm every subscriber " +
                    "unsubscribes on dispose (or switch to weak event patterns).",
            Score: Math.Min(score, 95),
            ContributingAreas: ["event-analysis", "memory-leak"]));
    }

    // ── Rule 3 — Thread pool saturation stalling async continuations ──────────
    private static void CheckThreadPoolSaturationWithAsyncBacklog(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t)
    {
        bool tpSaturated = s.TpMaxWorkers > 0 && s.TpActiveWorkers >= s.TpMaxWorkers * t.TpNearCapacityPct;
        bool blocked      = s.BlockedThreadCount > t.BlockedWarn;
        bool asyncBacklog = s.AsyncBacklogTotal > t.AsyncWarn;

        if (!tpSaturated || !(blocked || asyncBacklog)) return;

        bool critTier = s.TpActiveWorkers >= s.TpMaxWorkers &&
                        (s.BlockedThreadCount > t.BlockedCrit || s.AsyncBacklogTotal > t.AsyncCrit);
        int score = 60 + (critTier ? 25 : 0) + (blocked ? 5 : 0) + (asyncBacklog ? 5 : 0);

        var effects = new List<string>();
        if (blocked)      effects.Add($"{s.BlockedThreadCount:N0} threads blocked");
        if (asyncBacklog) effects.Add($"{s.AsyncBacklogTotal:N0} async continuations suspended");

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Threading / Async",
            Headline: "Thread pool saturation is stalling async work and blocking threads",
            Detail: $"{s.TpActiveWorkers}/{s.TpMaxWorkers} thread pool workers active — {string.Join(" and ", effects)}. " +
                    "A saturated pool can't schedule new continuations, so async backlog and blocked threads " +
                    "compound each other rather than being independent problems.",
            Advice: "Run 'thread-pool <dump>' to confirm saturation, then 'async-stacks <dump>' to see what the " +
                    "suspended continuations are waiting on. Look for synchronous blocking calls (.Result/.Wait()) " +
                    "on pool threads — they're the usual cause.",
            Score: Math.Min(score, 95),
            ContributingAreas: ["thread-pool", "async-stacks", "thread-analysis"]));
    }

    // ── Rule 4 — Pinned handles fragmenting the heap ───────────────────────────
    private static void CheckFragmentationWithPinning(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t)
    {
        if (s.FragmentationPct < t.FragWarnPct || s.PinnedHandleCount <= t.PinnedWarn) return;

        bool critTier = s.FragmentationPct >= t.FragCritPct;
        int score = 60 + (critTier ? 20 : 0) + (s.PinnedHandleCount > t.PinnedWarn * 2 ? 10 : 0);

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Memory / GC",
            Headline: "Pinned handles are fragmenting the heap and limiting GC compaction",
            Detail: $"{s.FragmentationPct:F1}% of the heap is free-but-unusable space while " +
                    $"{s.PinnedHandleCount:N0} GC handles are pinned. The GC cannot move pinned objects, so it " +
                    "compacts around them, leaving the surrounding free space fragmented rather than reclaimable.",
            Advice: "Run 'pinned-objects <dump>' to find what's pinning, then 'heap-fragmentation <dump>' to see the " +
                    "segment-level impact. Replace GCHandle.Alloc(Pinned) with Memory<T>/MemoryPool<T> where possible.",
            Score: Math.Min(score, 90),
            ContributingAreas: ["pinned-objects", "heap-fragmentation"]));
    }

    // ── Rule 5 — Multiple unmanaged resource leaks converging ──────────────────
    private static void CheckMultiResourceLeakConvergence(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t)
    {
        var offenders = new List<string>(3);
        if (s.TimerCount > t.TimerWarn)             offenders.Add($"{s.TimerCount:N0} timers");
        if (s.WcfFaultedCount >= t.WcfFaultedWarn)   offenders.Add($"{s.WcfFaultedCount:N0} faulted WCF channel(s)");
        if (s.ConnectionCount > t.DbConnectionWarn)  offenders.Add($"{s.ConnectionCount:N0} DB connection objects");

        if (offenders.Count < 2) return;

        bool critTier = offenders.Count >= 3 || s.ConnectionCount > t.DbConnectionCrit;
        int score = 55 + offenders.Count * 10 + (critTier ? 10 : 0);

        var areas = new List<string>(3);
        if (s.TimerCount > t.TimerWarn)            areas.Add("timer-leaks");
        if (s.WcfFaultedCount >= t.WcfFaultedWarn)  areas.Add("wcf-channels");
        if (s.ConnectionCount > t.DbConnectionWarn) areas.Add("connection-pool");

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Infrastructure",
            Headline: "Multiple unmanaged resource types are leaking at once",
            Detail: $"{string.Join(", ", offenders)} — independently each is a moderate signal, but this many " +
                    "distinct unmanaged-resource leaks at once usually points to a single systemic cause " +
                    "(a shared base class, DI lifetime misconfiguration, or a missing Dispose in a common code path) " +
                    "rather than unrelated bugs.",
            Advice: "Check for a shared pattern across the affected types first — e.g. a base class or DI " +
                    "registration that's scoped wrong — before treating each leak as a separate fix.",
            Score: Math.Min(score, 90),
            ContributingAreas: areas.ToArray()));
    }

    // ── Rule 6 — Overall heap growth is Gen2-driven ─────────────────────────────
    private static void CheckLargeHeapDrivenByGen2(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t, double gen2Pct)
    {
        if (s.TotalHeapBytes <= t.HeapWarnMb * 1024L * 1024 || gen2Pct < t.Gen2WarnPct) return;

        bool critTier = s.TotalHeapBytes > t.HeapCritGb * 1024L * 1024 * 1024 && gen2Pct >= t.Gen2CritPct;
        int score = 55 + (critTier ? 25 : 0) + (gen2Pct >= t.Gen2CritPct ? 10 : 0);

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Memory / GC",
            Headline: "Heap growth is Gen2-driven, not transient allocation",
            Detail: $"Total managed heap is {DumpHelpers.FormatSize(s.TotalHeapBytes)}, and Gen2 alone accounts for " +
                    $"{gen2Pct:F0}% of it ({DumpHelpers.FormatSize(s.Gen2Bytes)}). A heap this large would be less " +
                    "concerning if it were mostly Gen0/Gen1 (short-lived allocation churn) — Gen2 dominance means " +
                    "the size is coming from objects that survived collection, i.e. retention, not throughput.",
            Advice: "Run 'gen-summary <dump>' to confirm the generation split, then 'memory-leak <dump>' for the " +
                    "retaining GC root chains.",
            Score: Math.Min(score, 90),
            ContributingAreas: ["gen-summary", "memory-leak"]));
    }

    // ── Rule 7 — LOH growth fragmenting the heap ────────────────────────────────
    private static void CheckLohGrowthWithFragmentation(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t)
    {
        if (s.LohBytes <= t.LohWarnMb * 1024L * 1024 || s.FragmentationPct < t.FragWarnPct) return;

        bool critTier = s.FragmentationPct >= t.FragCritPct;
        int score = 55 + (critTier ? 20 : 0) + (s.LohFragmentationPct >= 30 ? 10 : 0);

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Memory / GC",
            Headline: "Large object allocations are fragmenting the heap",
            Detail: $"LOH holds {DumpHelpers.FormatSize(s.LohBytes)} ({s.LohFragmentationPct:F0}% of it free-but-" +
                    $"unreclaimed) while overall heap fragmentation sits at {s.FragmentationPct:F1}%. The LOH is not " +
                    "compacted by default, so large allocate/free cycles leave holes that smaller objects can't " +
                    "reuse — the two signals are one mechanism, not two.",
            Advice: "Run 'large-objects <dump>' to find what's allocating >85 KB, then pool those buffers " +
                    "(ArrayPool<T>/MemoryPool<T>) or enable LOH compaction " +
                    "(GCSettings.LargeObjectHeapCompactionMode).",
            Score: Math.Min(score, 90),
            ContributingAreas: ["large-objects", "heap-fragmentation"]));
    }

    // ── Rule 8 — Exception churn coinciding with heap pressure ─────────────────
    private static void CheckExceptionStormWithHeapPressure(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t, double gen2Pct)
    {
        if (s.ExceptionThreadCount <= t.ExceptionWarn || gen2Pct < t.Gen2WarnPct) return;

        bool critTier = gen2Pct >= t.Gen2CritPct && s.ExceptionThreadCount > t.ExceptionWarn * 2;
        int score = 55 + (critTier ? 20 : 0);

        string? topEx = s.ExceptionCounts.FirstOrDefault() is { } e ? $"{e.Name} ×{e.Count:N0}" : null;

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Exceptions / Memory",
            Headline: "Exception churn is coinciding with heap pressure",
            Detail: $"{s.ExceptionThreadCount:N0} thread(s) show active exceptions while Gen2 holds {gen2Pct:F0}% of " +
                    $"the heap.{(topEx is not null ? $" Most common: {topEx}." : "")} Exceptions are relatively " +
                    "expensive to construct and unwind (stack traces, wrapped inner exceptions); a storm of them " +
                    "under heap pressure can be cause or effect of the pressure rather than an unrelated failure.",
            Advice: "Run 'exception-analysis <dump>' for the exception objects and stacks, and check whether the " +
                    "exception type itself (or its captured state) is what's accumulating in Gen2.",
            Score: Math.Min(score, 85),
            ContributingAreas: ["exception-analysis", "gen-summary"]));
    }

    // ── Rule 9 — DB connection exhaustion + faulted WCF channels ───────────────
    private static void CheckConnectionExhaustionWithWcfFaults(
        List<CorrelationFinding> findings, DumpSnapshot s, ScoringThresholds t)
    {
        if (s.ConnectionCount <= t.DbConnectionWarn || s.WcfFaultedCount < t.WcfFaultedWarn) return;

        bool critTier = s.ConnectionCount > t.DbConnectionCrit;
        int score = 55 + (critTier ? 20 : 0) + (s.WcfFaultedCount > 3 ? 10 : 0);

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Infrastructure",
            Headline: "Outbound dependency failures are showing up on two channels at once",
            Detail: $"{s.ConnectionCount:N0} DB connection objects and {s.WcfFaultedCount:N0} faulted WCF channel(s) " +
                    "are both live. DB and WCF failures happening together often means the failure is upstream of " +
                    "both — a shared network path, downstream outage, or timeout misconfiguration — rather than " +
                    "two independent leaks.",
            Advice: "Run 'connection-pool <dump>' and 'wcf-channels <dump>' together and compare fault timing/" +
                    "endpoints before assuming they're separate issues.",
            Score: Math.Min(score, 85),
            ContributingAreas: ["connection-pool", "wcf-channels"]));
    }

    // ── Rule 10 — Generic structural safety net ─────────────────────────────────
    // Doesn't reason about *why* signals relate — just notices that Findings already
    // span several independent domains, which alone is worth flagging as "possibly one
    // root cause" even when no specific named rule above matched.
    private static void CheckBroadDomainConvergence(List<CorrelationFinding> findings, DumpSnapshot s)
    {
        var byCategory = s.Findings
            .Where(f => f.Category != "Summary")
            .GroupBy(f => f.Category)
            .ToList();

        if (byCategory.Count < 3) return;

        int criticalCount = s.Findings.Count(f => f.Severity == FindingSeverity.Critical);
        if (criticalCount < 2) return;

        bool critTier = criticalCount >= 4 && byCategory.Count >= 4;
        int score = 40 + Math.Min(byCategory.Count * 5, 20) + Math.Min(criticalCount * 3, 15);

        var categories = byCategory.Select(g => g.Key).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var areas = categories
            .Select(TargetCommandForCategory)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct()
            .ToArray();

        findings.Add(new CorrelationFinding(
            Severity: critTier ? FindingSeverity.Critical : FindingSeverity.Warning,
            Category: "Cross-Domain",
            Headline: $"Findings span {byCategory.Count} independent domains ({criticalCount} critical)",
            Detail: $"Categories involved: {string.Join(", ", categories)}. No single named correlation rule " +
                    "matched this exact combination, but this many simultaneous problems across unrelated domains " +
                    "is itself a signal — it's worth checking whether one upstream cause (e.g. a stuck downstream " +
                    "dependency, a bad deploy, or resource exhaustion at the process/host level) explains several " +
                    "of these at once before investigating each domain independently.",
            Advice: "Start from the Action Queue's 'Now' bucket — it's already ranked by severity + magnitude — " +
                    "and check whether the top 2-3 items share a plausible common trigger before treating the rest " +
                    "as separate root causes.",
            Score: Math.Min(score, 75),
            ContributingAreas: areas));
    }

    private static string? TargetCommandForCategory(string category) => category switch
    {
        "Memory"      => "heap-stats",
        "Memory Leak" => "memory-leak",
        "Leaks"       => "event-analysis",
        "Async"       => "async-stacks",
        "Threading"   => "thread-analysis",
        "Exceptions"  => "exception-analysis",
        "WCF"         => "wcf-channels",
        "Connections" => "connection-pool",
        _             => null,
    };
}
