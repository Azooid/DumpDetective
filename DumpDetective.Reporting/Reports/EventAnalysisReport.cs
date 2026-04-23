using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class EventAnalysisReport
{
    public void Render(EventAnalysisData data, IRenderSink sink, int top = 20, bool showAddr = false)
    {
        sink.Section("1. Event Handler Leaks");

        sink.Explain(
            what: "Event handler subscriptions found on the managed heap. Each row represents an event field " +
                  "on a publisher object that currently holds one or more subscriber delegate references.",
            why:  "When a subscriber registers a handler with '+=' but never calls '-=', the publisher's event field " +
                  "holds a delegate reference to the subscriber. This prevents the garbage collector from reclaiming " +
                  "the subscriber — and everything the subscriber references — for as long as the publisher is alive.",
            bullets:
            [
                "Static publishers → subscribers will NEVER be garbage collected (GC cannot collect static roots)",
                "Long-lived publishers (singletons, services) → subscribers outlive their intended lifetime",
                "Lambda/closure handlers → the closure captures additional objects in its scope, expanding the retained graph",
                "Duplicate subscriptions → '+=' called multiple times without '-=' matching each call",
            ],
            impact: "Event leaks cause silent, unbounded memory growth. Unlike object leaks that show up in heap stats, " +
                    "event-leaked objects appear 'reachable' from the GC's perspective — they look like intentional retention. " +
                    "A single static event field can prevent thousands of objects from ever being collected.",
            action: "For each publisher.Field with high subscribers: verify '-= handler' is called in Dispose(), " +
                    "during component shutdown, or when the subscriber's lifecycle ends. " +
                    "Use WeakEventManager or CompositeDisposable for complex subscription lifetimes.");

        if (data.Groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No event handler leaks found.");
            return;
        }

        int totalSubs  = data.Groups.Sum(g => g.Subscribers);
        int leakGroups = data.Groups.Count(g => g.Subscribers > 5);

        RenderSummaryTable(sink, data, top);
        RenderSubscriberBreakdown(sink, data, top);
        RenderTopMethods(sink, data, top);
    }

    private static void RenderSummaryTable(IRenderSink sink, EventAnalysisData data, int top)
    {
        var rows = data.Groups.Take(top).Select(g =>
        {
            string sev = g.IsStaticPublisher ? "⚡ CRITICAL" : g.HasStaticSubs ? "⚠ WARNING" : "—";
            return new[]
            {
                g.Publisher, g.Field,
                g.InstanceCount > 0 ? g.InstanceCount.ToString("N0") : "1",
                g.Subscribers.ToString("N0"),
                DumpHelpers.FormatSize(g.RetainedBytes),
                sev,
            };
        }).ToList();

        sink.Table(
            ["Publisher Type", "Event Field", "Instances", "Subscribers", "Retained", "Severity"],
            rows,
            $"{data.Groups.Count} unique event field(s) across {data.PublisherInstanceCount:N0} publisher instance(s)");
    }

    private static void RenderSubscriberBreakdown(IRenderSink sink, EventAnalysisData data, int top)
    {
        sink.Section("2. Subscriber Breakdown");
        foreach (var g in data.Groups.Take(top))
        {
            string sev = g.IsStaticPublisher ? "⚡ CRITICAL" : g.HasStaticSubs ? "⚠ WARNING" : "—";
            sink.BeginDetails(
                $"{g.Publisher}.{g.Field}  ({g.Subscribers:N0} subscribers  |  {DumpHelpers.FormatSize(g.RetainedBytes)} retained  |  {sev})",
                open: g.IsStaticPublisher);

            if (g.AllSubs is { Count: > 0 } allSubs)
            {
                var bySubType = allSubs
                    .GroupBy(s => (s.TargetType, s.MethodName))
                    .Select(tg => (
                        Type:      tg.Key.TargetType,
                        Method:    tg.Key.MethodName,
                        Count:     tg.Count(),
                        Size:      tg.Sum(s => s.Size),
                        HasStatic: tg.Any(s => s.IsStaticRooted),
                        IsLambda:  tg.All(s => s.IsLambda)))
                    .OrderByDescending(t => t.Count)
                    .Take(10)
                    .ToList();

                sink.Table(
                    ["Subscriber Type", "Subscribed Method", "Count", "Size", "Static?", "Lambda?"],
                    bySubType.Select(t => new[]
                    {
                        t.Type, t.Method, t.Count.ToString("N0"),
                        DumpHelpers.FormatSize(t.Size),
                        t.HasStatic ? "⚠ yes" : "—",
                        t.IsLambda  ? "λ yes" : "—",
                    }).ToList());
            }
            else
            {
                sink.KeyValues([("Subscribers", g.Subscribers.ToString("N0"))]);
            }

            if (g.DuplicateCount > 0)
                sink.Alert(AlertLevel.Warning,
                    $"{g.DuplicateCount} duplicate subscription(s) on '{g.Field}'.",
                    advice: "Ensure += is not called multiple times without a matching -=.");
            if (g.IsStaticPublisher)
                sink.Alert(AlertLevel.Critical,
                    $"Static publisher: subscribers on '{g.Field}' will NEVER be garbage collected.",
                    advice: $"publisher.{g.Field} -= OnHandler;  // call in Dispose()");
            else if (g.HasStaticSubs)
                sink.Alert(AlertLevel.Warning,
                    $"Long-lived subscribers on '{g.Field}' are kept alive by static roots.",
                    advice: $"publisher.{g.Field} -= OnHandler;  // call in Dispose()");

            sink.EndDetails();
        }
    }

    private static void RenderTopMethods(IRenderSink sink, EventAnalysisData data, int top)
    {
        var allSubs = data.Groups
            .Where(g => g.AllSubs is not null)
            .SelectMany(g => g.AllSubs!)
            .ToList();
        if (allSubs.Count == 0) return;

        sink.Section("3. Top Subscribed Methods");
        var methodStats = allSubs
            .GroupBy(s => s.MethodName)
            .Select(mg => (
                Method:   mg.Key,
                Count:    mg.Count(),
                Size:     mg.Sum(s => s.Size),
                IsLambda: mg.Any(s => s.IsLambda)))
            .OrderByDescending(m => m.Count)
            .Take(top)
            .ToList();

        sink.Table(
            ["Subscribed Method", "Total Subscriptions", "Retained", "Lambda?"],
            methodStats.Select(m => new[]
            {
                m.Method, m.Count.ToString("N0"),
                DumpHelpers.FormatSize(m.Size),
                m.IsLambda ? "λ yes" : "—",
            }).ToList(),
            "Methods sorted by subscription count across all events");

        // Footer: severity alert + key-value summary (matches old RenderFooter)
        int   totalSubs     = data.Groups.Sum(g => g.Subscribers);
        long  totalRetained = data.Groups.Sum(g => g.RetainedBytes);
        int   criticalCount = data.Groups.Count(g => g.IsStaticPublisher);
        int   warningCount  = data.Groups.Count(g => !g.IsStaticPublisher && g.HasStaticSubs);
        int   totalLambdas  = data.Groups.Sum(g => g.LambdaCount);
        int   totalDupes    = data.Groups.Sum(g => g.DuplicateCount);
        int   publisherInstCount = data.PublisherInstanceCount;

        if (criticalCount > 0)
            sink.Alert(AlertLevel.Critical,
                $"{criticalCount} static publisher(s) — subscribers are permanently rooted and will never be collected.");
        else if (totalSubs > 1000)
            sink.Alert(AlertLevel.Critical, $"High subscriber count: {totalSubs:N0} total event subscriptions.",
                advice: "Unsubscribe event handlers when the subscriber is disposed (-= handler).");
        else if (totalSubs > 200 || warningCount > 0)
            sink.Alert(AlertLevel.Warning, $"{totalSubs:N0} event subscriptions found.");

        sink.KeyValues([
            ("Unique event fields",      data.Groups.Count.ToString("N0")),
            ("Publisher instances",      publisherInstCount.ToString("N0")),
            ("Total subscribers",        totalSubs.ToString("N0")),
            ("Lambda/closure subs",      totalLambdas.ToString("N0")),
            ("Duplicate subscriptions",  totalDupes.ToString("N0")),
            ("Total retained memory",    DumpHelpers.FormatSize(totalRetained)),
            ("Static publishers",        criticalCount.ToString("N0")),
        ]);
    }
}
