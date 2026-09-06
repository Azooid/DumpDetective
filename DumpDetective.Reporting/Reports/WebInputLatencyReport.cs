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
                  "async InputLatency events rather than estimated.",
            why:  "A slow interaction is felt directly by the user as unresponsiveness — the page 'not reacting'. " +
                  "It's almost always caused by the main thread being blocked by something else, not the " +
                  "interaction handler itself being slow.",
            bullets:
            [
                "P95 > 200ms → Core Web Vitals 'needs improvement' territory for INP",
                "A single multi-second outlier → the main thread was fully blocked, not just busy",
            ],
            action: "Cross-reference the worst interactions' timestamps with web-long-tasks / web-cpu-hotspots.");

        sink.KeyValues([
            ("Interactions measured", data.SampleCount.ToString("N0")),
            ("Median latency",        $"{data.MedianUs / 1000.0:F0} ms"),
            ("P95 latency",           $"{data.P95Us / 1000.0:F0} ms"),
            ("Worst latency",         $"{data.MaxUs / 1000.0:F0} ms"),
        ]);

        if (data.WorstUs.Count > 0)
            sink.Table(
                ["Latency"],
                data.WorstUs.Select(us => new[] { $"{us / 1000.0:F0} ms" }).ToList(),
                "Worst 20 interactions, descending.");

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
