using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class EventAnalysisReport
{
    public void Render(EventAnalysisData data, IRenderSink sink, int top = 20, bool showAddr = false)
    {
        sink.Section("1. Event Handler Leaks");

        if (data.Groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No event handler leaks found.");
            return;
        }

        int totalSubs   = data.Groups.Sum(g => g.Subscribers);
        int leakGroups  = data.Groups.Count(g => g.Subscribers > 5);

        sink.KeyValues([
            ("Publisher / field pairs with subscribers", data.Groups.Count.ToString("N0")),
            ("Total live subscribers", totalSubs.ToString("N0")),
            ("Fields with > 5 subscribers (potential leaks)", leakGroups.ToString("N0")),
        ]);

        if (leakGroups > 0)
            sink.Alert(AlertLevel.Warning, $"{leakGroups} event field(s) with > 5 subscribers.",
                "Event handlers that are never unsubscribed hold the subscriber alive.",
                "Ensure your subscribers call -= or implement IDisposable.");

        RenderSummaryTable(sink, data, top);
        RenderPublisherBreakdown(sink, data, top);
    }

    private static void RenderSummaryTable(IRenderSink sink, EventAnalysisData data, int top)
    {
        var rows = data.Groups
            .Take(top)
            .Select(g => new[]
            {
                g.Publisher,
                g.Field,
                g.Subscribers.ToString("N0"),
                g.Subscribers > 100 ? "High" : g.Subscribers > 10 ? "Medium" : "Low",
            }).ToList();

        sink.Table(["Publisher Type", "Event Field", "Subscriber Count", "Severity"], rows,
            $"Top {rows.Count} of {data.Groups.Count} publisher/field pairs  |  sorted by subscriber count");
    }

    private static void RenderPublisherBreakdown(IRenderSink sink, EventAnalysisData data, int top)
    {
        sink.Section("2. Publisher Type Breakdown");
        var publisherRows = data.Groups
            .GroupBy(g => g.Publisher)
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                g.Sum(x => x.Subscribers).ToString("N0"),
            })
            .OrderByDescending(r => int.Parse(r[2].Replace(",", "")))
            .Take(top)
            .ToList();

        sink.Table(["Publisher Type", "Fields", "Total Subscribers"], publisherRows);
    }
}
