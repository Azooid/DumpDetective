using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Frame-drop rate from <c>BeginFrame</c>/<c>DroppedFrame</c> compositor events — a
/// coarse but cheap jank signal (finer-grained "worst window" analysis is a natural
/// follow-on, not required for a useful first cut).
/// </summary>
public static class WebJankAnalyzer
{
    private const double DropRateCritical = 0.25; // 25%
    private const double DropRateWarn     = 0.10; // 10%

    public static WebJankData Analyze(WebTraceData data, string traceFileName)
    {
        double dropRate = data.BeginFrameCount > 0
            ? (double)data.DroppedFrameCount / data.BeginFrameCount
            : 0;
        double durationSec = data.DurationUs / 1_000_000.0;
        double fps = durationSec > 0
            ? (data.BeginFrameCount - data.DroppedFrameCount) / durationSec
            : 0;

        var findings = new List<Finding>();
        if (dropRate >= DropRateCritical)
        {
            findings.Add(new Finding(FindingSeverity.Critical, "Web Rendering",
                $"{dropRate * 100:F0}% of frames were dropped ({data.DroppedFrameCount:N0} / {data.BeginFrameCount:N0})",
                Advice: "This heavy a drop rate is visible as stutter/jank to any user. Check web-cpu-hotspots and " +
                        "web-long-tasks for what's competing with the compositor for main-thread time.",
                Deduction: 25));
        }
        else if (dropRate >= DropRateWarn)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web Rendering",
                $"{dropRate * 100:F0}% of frames were dropped ({data.DroppedFrameCount:N0} / {data.BeginFrameCount:N0})",
                Deduction: 10));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                data.BeginFrameCount == 0
                    ? "No frame data captured in this recording."
                    : "Frame drop rate looks normal for this recording."));

        return new WebJankData
        {
            TraceInfo         = traceFileName,
            BeginFrameCount   = data.BeginFrameCount,
            DroppedFrameCount = data.DroppedFrameCount,
            DropRatePct       = dropRate * 100,
            EffectiveFps      = fps,
            Findings          = findings,
        };
    }
}
