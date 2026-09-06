using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class WebInputLatencyReport
{
    public void Render(WebInputLatencyData data, IRenderSink sink)
    {
        sink.Section("Input Latency");
        sink.Explain(
            what: "Time from a user interaction (click/scroll/move) to the browser finishing processing it — an " +
                  "'Interaction to Next Paint'-style responsiveness signal, measured directly from the trace's " +
                  "async InputLatency events rather than estimated. Each interaction below is identified by kind " +
                  "(e.g. MouseDown, GestureScrollUpdate) and, when a long task overlapped it, which function was " +
                  "blocking the main thread at the time — already cross-referenced against web-long-tasks so it " +
                  "doesn't need doing by hand.",
            why:  "A slow interaction is felt directly by the user as unresponsiveness — the page 'not reacting'. " +
                  "It's almost always caused by the main thread being blocked by something else, not the " +
                  "interaction handler itself being slow.",
            bullets:
            [
                "P95 > 200ms → Core Web Vitals 'needs improvement' territory for INP",
                "A single multi-second outlier → the main thread was fully blocked, not just busy",
                "'Likely Blocked By' is '—' → no long task overlapped it; the interaction handler itself is the " +
                  "likely cost, not something else blocking it",
            ],
            action: "Fix the top row's blocker first — it's what the main thread was actually doing while the user waited.");

        sink.KeyValues([
            ("Interactions measured", data.SampleCount.ToString("N0")),
            ("Median latency",        $"{data.MedianUs / 1000.0:F0} ms"),
            ("P95 latency",           $"{data.P95Us / 1000.0:F0} ms"),
            ("Worst latency",         $"{data.MaxUs / 1000.0:F0} ms"),
        ]);

        if (data.WorstEvents.Count > 0)
            sink.Table(
                ["Interaction", "Latency", "Timestamp (trace-relative)", "Likely Blocked By", "Location"],
                data.WorstEvents.Select(e => new[]
                {
                    e.Kind,
                    $"{e.DurationUs / 1000.0:F0} ms",
                    $"{e.TimestampUs / 1000.0:F0} ms",
                    e.BlockedByFunction ?? "—",
                    e.BlockedByLocation,
                }).ToList(),
                "Worst 20 interactions, descending. Likely Blocked By is the long task (if any) whose window " +
                "overlapped the interaction's start — what the main thread was actually busy doing while the user " +
                "waited; '—' means no long task overlapped it (the interaction handler itself is the likely cost).");

        sink.Section("Findings");
        foreach (var f in data.Findings)
            sink.Alert(
                f.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                        => AlertLevel.Info,
                },
                f.Headline, f.Detail, f.Advice);
    }
}
