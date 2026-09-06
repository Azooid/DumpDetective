using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Summarizes completed <c>InputLatency::*</c> durations (click/scroll/move-to-response
/// timing) — an "Interaction to Next Paint"-style signal for how responsive the page
/// actually felt.
/// </summary>
public static class WebInputLatencyAnalyzer
{
    private const long SlowInteractionUsCritical = 1_000_000; // 1s — well past INP "poor" threshold
    private const long SlowInteractionUsWarn     = 200_000;   // 200ms — INP "needs improvement" threshold

    public static WebInputLatencyData Analyze(WebTraceData data, string traceFileName)
    {
        var sorted = data.InputLatenciesUs.OrderBy(x => x).ToList();
        int n = sorted.Count;

        long median = n > 0 ? sorted[n / 2] : 0;
        long p95    = n > 0 ? sorted[(int)Math.Min(n - 1, Math.Ceiling(n * 0.95) - 1)] : 0;
        long max     = n > 0 ? sorted[^1] : 0;
        var worst    = data.InputLatenciesUs.OrderByDescending(x => x).Take(20).ToList();

        var findings = new List<Finding>();
        if (max >= SlowInteractionUsCritical)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Performance",
                $"Slowest interaction took {max / 1_000_000.0:F1}s to respond",
                Advice: "An interaction this slow means the page was completely unresponsive to the user for that " +
                        "long — almost always caused by a blocked main thread (check web-long-tasks / web-cpu-hotspots " +
                        "for what was running at that timestamp), not the interaction itself being expensive.",
                Deduction: 30));
        }
        else if (p95 >= SlowInteractionUsWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Performance",
                $"95th-percentile interaction latency is {p95 / 1000.0:F0}ms (INP 'needs improvement' threshold is 200ms)",
                Deduction: 15));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                n == 0
                    ? "No input events captured in this recording."
                    : "Interaction latency looks responsive throughout this recording."));

        return new WebInputLatencyData
        {
            TraceInfo   = traceFileName,
            SampleCount = n,
            MedianUs    = median,
            P95Us       = p95,
            MaxUs       = max,
            WorstUs     = worst,
            Findings    = findings,
        };
    }
}
