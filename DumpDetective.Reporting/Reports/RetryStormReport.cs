using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class RetryStormReport
{
    public void Render(RetryStormData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Retry storm detection from exception events — identifies windows where retry-pattern exceptions (Timeout, Transient, HttpRequest, etc.) occur at 2× or more the average rate.",
            why: "Aggressive retry logic without jitter amplifies load on a struggling dependency, turning a momentary hiccup into a cascading failure.",
            impact: "Each retry from every caller multiplies the request volume hitting the downstream service, delaying recovery and exhausting thread pool threads.",
            bullets: [
                "Retry bursts        — time windows with anomalously high retry exceptions",
                "Peak retry rate     — maximum retries per minute observed",
                "Affected types      — exception types matching retry patterns"
            ],
            action: "Add exponential backoff with jitter (Polly, IHttpClientFactory retry policy). " +
                    "Use circuit breaker to stop retrying when a dependency is down. " +
                    "Set reasonable max retry counts (3–5) with hard timeouts."
        );

        sink.Section("Trace Summary", "retry-summary");
        sink.KeyValues([
            ("Trace",               data.TraceInfo),
            ("Total retry exceptions",data.TotalRetryExceptions.ToString("N0")),
            ("Burst periods",        data.BurstCount.ToString("N0")),
            ("Peak rate",            $"{data.PeakRetryRatePerMin:F0} /min"),
            ("Process filter",       data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No retry-pattern exceptions found in the trace.",
                "Retry detection requires exception events.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x8000:5' (ExceptionKeyword)");
            return;
        }

        if (data.BurstCount >= 3)
            sink.Alert(AlertLevel.Critical,
                $"Retry storm: {data.BurstCount} burst period(s), peak {data.PeakRetryRatePerMin:F0}/min.",
                "The application is repeatedly retrying failing calls. This amplifies load on the dependency.",
                "Add jitter to retry policies and implement circuit breakers.");
        else if (data.BurstCount >= 1)
            sink.Alert(AlertLevel.Warning,
                $"Retry burst(s) detected: {data.TotalRetryExceptions} total retry-pattern exceptions.",
                "Check downstream dependency health and retry policy configuration.");

        if (data.RetryTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Retry exceptions per minute", "/min");

        if (data.AffectedExceptionTypes.Count > 0)
        {
            sink.Section("Affected Exception Types", "retry-types");
            var rows = data.AffectedExceptionTypes
                .Take(top)
                .Select(t => new string[] { t })
                .ToList();
            sink.Table(["Exception Type"], rows, "Exception types matching retry patterns");
        }

        if (data.RetryBursts.Count > 0)
        {
            sink.Section("Retry Burst Periods", "retry-bursts");
            var rows = new List<string[]>(Math.Min(top, data.RetryBursts.Count));
            foreach (var b in data.RetryBursts.Take(top))
                rows.Add([$"{b.StartMs:F0} ms", $"{b.EndMs - b.StartMs:F0} ms",
                           b.ExceptionCount.ToString("N0"), $"{b.RatePerMin:F0}/min"]);
            sink.Table(
                ["Start Offset", "Duration", "Exception Count", "Rate"],
                rows, "Burst periods ordered by exception count");
        }
    }
}
