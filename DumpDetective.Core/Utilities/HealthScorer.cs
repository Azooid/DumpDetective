using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Evaluates a <see cref="DumpSnapshot"/> against <see cref="ScoringThresholds"/>
/// and produces a list of <see cref="Finding"/> records plus a health score (0–100).
///
/// Extracted from <c>DumpCollector.GenerateFindings</c> so it can be unit-tested
/// without a real dump file — only plain POCOs are involved.
/// </summary>
public static class HealthScorer
{
    public static (IReadOnlyList<Finding> Findings, int Score) Score(
        DumpSnapshot s,
        ScoringThresholds t)
    {
        int score    = 100;
        var findings = new List<Finding>();

        void Add(FindingSeverity sev, string cat, string headline,
                 string? detail = null, string? advice = null, int deduct = 0)
        {
            findings.Add(new Finding(sev, cat, headline, detail, advice, deduct));
            score = Math.Max(0, score - deduct);
        }

        // ── Memory ────────────────────────────────────────────────────────────
        if (s.TotalHeapBytes > t.HeapCritGb * 1024L * 1024 * 1024)
            Add(FindingSeverity.Critical, "Memory",
                $"Heap exceeds {t.HeapCritGb} GB ({DumpHelpers.FormatSize(s.TotalHeapBytes)})",
                advice: "Check top memory consumers for leaked collections or cached data.",
                deduct: t.DeductHeapCrit);
        else if (s.TotalHeapBytes > t.HeapWarnMb * 1024L * 1024)
            Add(FindingSeverity.Warning, "Memory",
                $"Heap is large ({DumpHelpers.FormatSize(s.TotalHeapBytes)})",
                deduct: t.DeductHeapWarn);

        if (s.LohBytes > t.LohWarnMb * 1024L * 1024)
            Add(FindingSeverity.Warning, "Memory",
                $"LOH is {DumpHelpers.FormatSize(s.LohBytes)}",
                detail: "Large Object Heap cannot be compacted by default.",
                advice: "Pool large byte arrays with ArrayPool<T>.",
                deduct: t.DeductLoh);

        if (s.FragmentationPct >= t.FragCritPct)
            Add(FindingSeverity.Critical, "Memory",
                $"Heap fragmentation {s.FragmentationPct:F1}%",
                advice: "Reduce GCHandle.Alloc(Pinned) and use Memory<T> for I/O buffers.",
                deduct: t.DeductFragCrit);
        else if (s.FragmentationPct >= t.FragWarnPct)
            Add(FindingSeverity.Warning, "Memory",
                $"Heap fragmentation {s.FragmentationPct:F1}%",
                deduct: t.DeductFragWarn);

        // ── Finalizer queue ───────────────────────────────────────────────────
        if (s.FinalizerQueueDepth > t.FinalizerCrit)
            Add(FindingSeverity.Critical, "Memory",
                $"Finalizer queue has {s.FinalizerQueueDepth:N0} objects",
                advice: "Call Dispose() / use 'using'. Objects on this queue delay GC.",
                deduct: t.DeductFinalizerCrit);
        else if (s.FinalizerQueueDepth > t.FinalizerWarn)
            Add(FindingSeverity.Warning, "Memory",
                $"Finalizer queue depth: {s.FinalizerQueueDepth:N0}",
                deduct: t.DeductFinalizerWarn);

        // ── Pinned handles ────────────────────────────────────────────────────
        if (s.PinnedHandleCount > t.PinnedWarn)
            Add(FindingSeverity.Warning, "Memory",
                $"{s.PinnedHandleCount:N0} pinned GC handles",
                advice: "Replace GCHandle.Alloc(Pinned) with Memory<T> / MemoryPool<T>.",
                deduct: t.DeductPinned);

        // ── Event leaks ───────────────────────────────────────────────────────
        if (s.EventLeakMaxOnField > t.EventPerFieldCrit)
            Add(FindingSeverity.Critical, "Leaks",
                $"Event leak: {s.EventLeakMaxOnField:N0} subscribers on a single field",
                detail: s.TopEventLeaks.FirstOrDefault() is { } el
                    ? $"{el.PublisherType}.{el.FieldName}" : null,
                advice: "Unsubscribe event handlers when the subscriber is disposed.",
                deduct: t.DeductEventCrit);
        else if (s.EventSubscriberTotal > t.EventTotalWarn)
            Add(FindingSeverity.Warning, "Leaks",
                $"Event leaks: {s.EventLeakFieldCount:N0} fields, {s.EventSubscriberTotal:N0} total subscribers",
                deduct: t.DeductEventWarn);

        // ── String waste ──────────────────────────────────────────────────────
        if (s.StringWastedBytes > t.StringWasteWarnMb * 1024L * 1024)
            Add(FindingSeverity.Warning, "Memory",
                $"String duplication wastes {DumpHelpers.FormatSize(s.StringWastedBytes)}",
                advice: "Use string interning or shared constants for repeated strings.",
                deduct: t.DeductString);

        // ── Async backlog ─────────────────────────────────────────────────────
        if (s.AsyncBacklogTotal > t.AsyncCrit)
            Add(FindingSeverity.Critical, "Async",
                $"{s.AsyncBacklogTotal:N0} async continuations suspended",
                detail: s.TopAsyncMethods.FirstOrDefault() is { } top
                    ? $"Top: {top.Name} ({top.Count:N0})" : null,
                advice: "Investigate awaited operations for I/O or lock contention.",
                deduct: t.DeductAsyncCrit);
        else if (s.AsyncBacklogTotal > t.AsyncWarn)
            Add(FindingSeverity.Warning, "Async",
                $"{s.AsyncBacklogTotal:N0} async continuations suspended",
                deduct: t.DeductAsyncWarn);

        // ── Thread pool ───────────────────────────────────────────────────────
        if (s.TpMaxWorkers > 0 && s.TpActiveWorkers >= s.TpMaxWorkers)
            Add(FindingSeverity.Critical, "Threading",
                $"Thread pool saturated ({s.TpActiveWorkers}/{s.TpMaxWorkers} workers)",
                advice: "Avoid blocking synchronous calls on thread pool threads.",
                deduct: t.DeductTpCrit);
        else if (s.TpMaxWorkers > 0 && s.TpActiveWorkers > s.TpMaxWorkers * t.TpNearCapacityPct)
            Add(FindingSeverity.Warning, "Threading",
                $"Thread pool near capacity ({s.TpActiveWorkers}/{s.TpMaxWorkers})",
                deduct: t.DeductTpWarn);

        // ── Blocked threads ───────────────────────────────────────────────────
        if (s.BlockedThreadCount > t.BlockedCrit)
            Add(FindingSeverity.Critical, "Threading",
                $"{s.BlockedThreadCount:N0} threads appear blocked",
                deduct: t.DeductBlockedCrit);
        else if (s.BlockedThreadCount > t.BlockedWarn)
            Add(FindingSeverity.Warning, "Threading",
                $"{s.BlockedThreadCount:N0} threads appear blocked",
                deduct: t.DeductBlockedWarn);

        // ── Exception threads ─────────────────────────────────────────────────
        if (s.ExceptionThreadCount > t.ExceptionWarn)
            Add(FindingSeverity.Warning, "Exceptions",
                $"{s.ExceptionThreadCount:N0} threads have active exceptions",
                deduct: t.DeductException);

        // ── WCF ───────────────────────────────────────────────────────────────
        if (s.WcfFaultedCount >= t.WcfFaultedWarn)
            Add(FindingSeverity.Warning, "WCF",
                $"{s.WcfFaultedCount:N0} faulted WCF channel(s)",
                advice: "Call Abort() on faulted channels and recreate them.",
                deduct: t.DeductWcf);

        // ── DB connections ────────────────────────────────────────────────────
        if (s.ConnectionCount > t.DbConnectionCrit)
            Add(FindingSeverity.Critical, "Connections",
                $"{s.ConnectionCount:N0} DB connection objects on heap",
                advice: "Wrap SqlConnection in 'using'. Verify connection pooling settings.",
                deduct: t.DeductDbCrit);
        else if (s.ConnectionCount > t.DbConnectionWarn)
            Add(FindingSeverity.Warning, "Connections",
                $"{s.ConnectionCount:N0} DB connection objects on heap",
                deduct: t.DeductDbWarn);

        // ── Timers ────────────────────────────────────────────────────────────
        if (s.TimerCount > t.TimerWarn)
            Add(FindingSeverity.Warning, "Leaks",
                $"{s.TimerCount:N0} timer objects on heap",
                advice: "Dispose System.Timers.Timer instances. Check for timer leak pattern.",
                deduct: t.DeductTimer);

        // ── Memory leak — Gen2 dominance ──────────────────────────────────────
        {
            double gen2Pct  = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;
            var    leakTypes = s.TopTypes
                .Where(tt => tt.Count >= t.LeakTypeMinCount)
                .OrderByDescending(tt => tt.Count)
                .Take(3)
                .ToList();

            string? leakDetail = leakTypes.Count > 0
                ? $"Top accumulating types: {string.Join("; ", leakTypes.Select(tt => $"{tt.Name} ×{tt.Count:N0}"))}"
                : null;

            if (gen2Pct >= t.Gen2CritPct)
                Add(FindingSeverity.Critical, "Memory Leak",
                    $"Gen2 holds {gen2Pct:F0}% of managed heap ({DumpHelpers.FormatSize(s.Gen2Bytes)})",
                    detail: leakDetail,
                    advice: "Run: memory-leak <dump>  for GC root chains to find the retaining object.",
                    deduct: t.DeductLeakCrit);
            else if (gen2Pct >= t.Gen2WarnPct)
                Add(FindingSeverity.Warning, "Memory Leak",
                    $"Gen2 holds {gen2Pct:F0}% of managed heap ({DumpHelpers.FormatSize(s.Gen2Bytes)})",
                    detail: leakDetail,
                    advice: "Run: memory-leak <dump>  for full Gen2/LOH breakdown and GC root chains.",
                    deduct: t.DeductLeakWarn);
            else
            {
                var appType = s.TopTypes
                    .Where(tt => !DumpHelpers.IsSystemType(tt.Name) && tt.Count >= t.LeakTypeMinCount * 5)
                    .OrderByDescending(tt => tt.Count)
                    .FirstOrDefault();

                if (appType is not null)
                    Add(FindingSeverity.Warning, "Memory Leak",
                        $"High instance count: {appType.Name} ×{appType.Count:N0}",
                        detail: $"Gen2 at {gen2Pct:F0}% — if this count is growing across dumps it indicates a leak.",
                        advice: "Run: memory-leak <dump>  to trace GC root chains for this type.",
                        deduct: t.DeductLeakWarn);
            }
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary", "No significant issues detected."));

        var sorted = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ToList();

        return (sorted, score);
    }
}
