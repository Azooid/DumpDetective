using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Ranks completed network requests (correlated by <c>requestId</c> across
/// ResourceSendRequest/ResourceReceiveResponse/ResourceFinish) by duration — surfaces
/// slow, failed, or unexpectedly large requests. Reference implementation of
/// <c>IWebSubAnalyzer</c> built against the parser's reduced data, not the raw stream.
/// </summary>
public static class WebNetworkAnalyzer
{
    private const long SlowRequestUsCritical = 5_000_000;  // 5s
    private const long SlowRequestUsWarn     = 1_000_000;  // 1s

    public static WebNetworkData Analyze(WebTraceData data, string traceFileName)
    {
        var rows = data.NetworkRequests
            .OrderByDescending(r => r.DurationUs)
            .Select(r => new WebNetworkRow
            {
                Url               = r.Url,
                Method            = r.Method,
                ResourceType      = r.ResourceType,
                DurationUs        = r.DurationUs,
                TimeToFirstByteUs = r.TimeToFirstByteUs,
                EncodedBytes      = r.EncodedBytes,
                Failed            = r.Failed,
                FromCache         = r.FromCache,
            })
            .ToList();

        int failedCount = data.NetworkRequests.Count(r => r.Failed);
        long totalBytes = data.NetworkRequests.Sum(r => r.EncodedBytes);

        var findings = new List<Finding>();
        var slowest = rows.FirstOrDefault();
        if (slowest is not null && slowest.DurationUs >= SlowRequestUsCritical)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Performance",
                $"Slowest request took {slowest.DurationUs / 1_000_000.0:F1}s — {Truncate(slowest.Url)}",
                Advice: "A request this slow blocks whatever depended on its response. Check whether it's on the " +
                        "critical rendering path, and whether the backend or the network is the bottleneck.",
                Deduction: 25));
        }
        else if (slowest is not null && slowest.DurationUs >= SlowRequestUsWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Performance",
                $"Slowest request took {slowest.DurationUs / 1000.0:F0}ms — {Truncate(slowest.Url)}",
                Deduction: 10));
        }

        if (failedCount > 0)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Performance",
                $"{failedCount} network request(s) failed during this recording",
                Deduction: 10));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                data.NetworkRequests.Count == 0
                    ? "No completed network requests found (this recording may not have captured page-load network activity)."
                    : "No slow or failed requests found."));

        return new WebNetworkData
        {
            TraceInfo     = traceFileName,
            Requests      = rows,
            TotalRequests = data.NetworkRequests.Count,
            FailedCount   = failedCount,
            TotalBytes    = totalBytes,
            Findings      = findings,
        };
    }

    private static string Truncate(string url) => url.Length > 80 ? "…" + url[^79..] : url;
}
