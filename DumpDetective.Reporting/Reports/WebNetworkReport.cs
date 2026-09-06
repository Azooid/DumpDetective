using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class WebNetworkReport
{
    public void Render(WebNetworkData data, IRenderSink sink)
    {
        sink.Section("Network");
        sink.Explain(
            what: "Network requests captured during the recording, correlated across send/response/finish and " +
                  "ranked by total duration.",
            why:  "A slow or failed request blocks whatever depended on it. This trace format only captures " +
                  "requests that were sent and finished while DevTools was recording — a long-lived session " +
                  "recording after page load may show few or none.",
            bullets:
            [
                "Duration includes queueing + network + server time — TTFB isolates just the wait for a response",
                "'from cache' requests cost nothing on the wire but still show here for completeness",
            ]);

        sink.KeyValues([
            ("Completed requests", data.TotalRequests.ToString("N0")),
            ("Failed requests",    data.FailedCount.ToString("N0")),
            ("Total bytes (encoded)", DumpHelpers.FormatSize(data.TotalBytes)),
        ]);

        if (data.Requests.Count > 0)
            sink.Table(
                ["Duration", "TTFB", "Method", "Type", "Size", "Status", "URL"],
                data.Requests.Take(50).Select(r => new[]
                {
                    $"{r.DurationUs / 1000.0:F0} ms",
                    $"{r.TimeToFirstByteUs / 1000.0:F0} ms",
                    r.Method ?? "—",
                    r.ResourceType ?? "—",
                    DumpHelpers.FormatSize(r.EncodedBytes),
                    r.Failed ? "Failed" : r.FromCache ? "Cache" : "OK",
                    r.Url.Length > 90 ? "…" + r.Url[^89..] : r.Url,
                }).ToList(),
                "Ranked by duration descending, top 50 shown.");

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
