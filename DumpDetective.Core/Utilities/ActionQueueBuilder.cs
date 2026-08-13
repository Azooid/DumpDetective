using DumpDetective.Core.Models;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Promotes <see cref="Finding"/> records (already produced by <see cref="HealthScorer"/>)
/// into a ranked, actionable queue — a deterministic re-presentation of the same data,
/// not a new analysis. No ClrMD types; pure POCO transform, unit-testable without a dump.
/// </summary>
public static class ActionQueueBuilder
{
    // Matches the reference triage layout this was modeled on: a handful of items
    // demand action right now, a few more are worth lining up next, the rest are
    // worth knowing about but don't need a human yet.
    private const int NowCount  = 3;
    private const int NextCount = 4;

    public static IReadOnlyList<ActionItem> Build(IReadOnlyList<Finding> findings)
    {
        // The synthetic "no significant issues" Info finding HealthScorer emits when
        // everything is healthy isn't an action — there's nothing to queue.
        var actionable = findings.Where(f => f.Category != "Summary").ToList();
        if (actionable.Count == 0) return [];

        var items = new List<ActionItem>(actionable.Count);
        for (int i = 0; i < actionable.Count; i++)
        {
            var f = actionable[i];
            int score = Math.Min(100, SeverityBaseScore(f.Severity) + f.Deduction);
            var bucket = i < NowCount ? ActionBucket.Now
                       : i < NowCount + NextCount ? ActionBucket.Next
                       : ActionBucket.Watch;

            items.Add(new ActionItem(
                Priority:       $"P{i + 1}",
                Score:          score,
                Bucket:         bucket,
                Severity:       f.Severity,
                Category:       f.Category,
                Headline:       f.Headline,
                Detail:         f.Detail,
                Advice:         f.Advice,
                SuggestedOwner: SuggestedOwnerFor(f.Category),
                TargetCommand:  TargetCommandFor(f.Category, f.Headline)));
        }

        return items;
    }

    private static int SeverityBaseScore(FindingSeverity sev) => sev switch
    {
        FindingSeverity.Critical => 90,
        FindingSeverity.Warning  => 60,
        _                        => 30,
    };

    // Generic team labels — DumpDetective has no ticketing/org integration, so this is
    // a suggestion to route triage, not a real assignment.
    private static string SuggestedOwnerFor(string category) => category switch
    {
        "Memory" or "Memory Leak"      => "Platform / Memory Team",
        "Connections"                  => "Data / Platform Team",
        "WCF"                          => "Integration Team",
        "Leaks" or "Async" or
        "Threading" or "Exceptions"    => "App / Service Team",
        _                               => "Service Owner",
    };

    // Same keyword sniffing AnalyzeReport.Evidence() already uses to pick the most
    // relevant snapshot data per finding — reused here to pick the most relevant
    // sub-report to jump to.
    private static string? TargetCommandFor(string category, string headline)
    {
        string h = headline.ToLowerInvariant();
        return category switch
        {
            "Memory" when h.Contains("finalizer")    => "finalizer-queue",
            "Memory" when h.Contains("fragment")     => "heap-fragmentation",
            "Memory" when h.Contains("loh")           => "large-objects",
            "Memory" when h.Contains("pinned")        => "pinned-objects",
            "Memory" when h.Contains("string")        => "string-duplicates",
            "Memory"                                  => "heap-stats",
            "Memory Leak"                             => "memory-leak",
            "Leaks" when h.Contains("event")          => "event-analysis",
            "Leaks" when h.Contains("timer")          => "timer-leaks",
            "Leaks"                                   => "handle-table",
            "Async"                                   => "async-stacks",
            "Threading" when h.Contains("blocked")    => "thread-analysis",
            "Threading"                               => "thread-pool",
            "Exceptions"                               => "exception-analysis",
            "WCF"                                      => "wcf-channels",
            "Connections"                              => "connection-pool",
            _                                           => null,
        };
    }
}
