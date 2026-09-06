using DumpDetective.Analysis.WebTrace.Model;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.WebTrace.Analysis;

/// <summary>
/// Summarizes V8 GC pause activity (<c>V8.GC_MARK_COMPACTOR</c> / <c>V8.GC_SCAVENGER</c>
/// root spans, one per full GC cycle) captured during the streaming pass.
/// </summary>
public static class WebGcPressureAnalyzer
{
    private const long BlockingPauseUs = 200_000; // 200ms — a single GC pause a user would notice as a stutter

    public static WebGcPressureData Analyze(WebTraceData data, string traceFileName)
    {
        var pauses = data.GcEvents
            .OrderByDescending(g => g.DurationUs)
            .Select(g => new WebGcPauseRow { TimestampUs = g.TimestampUs, DurationUs = g.DurationUs, Kind = g.Kind })
            .ToList();

        int majorCount = data.GcEvents.Count(g => g.Kind == "Major");
        int minorCount = data.GcEvents.Count(g => g.Kind == "Minor");
        long totalUs   = data.GcEvents.Sum(g => g.DurationUs);
        long longestUs = data.GcEvents.Count > 0 ? data.GcEvents.Max(g => g.DurationUs) : 0;

        var findings = new List<Finding>();
        if (longestUs >= BlockingPauseUs)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web GC",
                $"Longest GC pause was {longestUs / 1000.0:F0} ms — long enough to be felt as a stutter",
                Advice: "A GC pause this long usually means a large amount of live/garbage data accumulated between " +
                        "collections. Cross-reference with web-memory-leak — sustained heap growth right before a " +
                        "long pause is a strong pairing.",
                Deduction: 15));
        }

        if (majorCount >= 10)
        {
            findings.Add(new Finding(FindingSeverity.Warning, "Web GC",
                $"{majorCount} major (full) GC cycles in this recording",
                Advice: "Frequent major GCs mean objects are surviving into old space faster than expected — " +
                        "check web-memory-leak for what's growing.",
                Deduction: 10));
        }

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary",
                data.GcEvents.Count == 0
                    ? "No major/minor GC cycles recorded (short recording, or GC simply wasn't triggered)."
                    : "GC pause activity looks normal for this recording's length."));

        return new WebGcPressureData
        {
            TraceInfo     = traceFileName,
            Pauses        = pauses,
            MajorCount    = majorCount,
            MinorCount    = minorCount,
            TotalPauseUs  = totalUs,
            LongestPauseUs = longestUs,
            Findings      = findings,
        };
    }
}
