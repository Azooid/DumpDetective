using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// A single time-bounded event slice on the unified cross-analyzer timeline.
/// All analyzers contribute slices; the timeline renders them in chronological order
/// so events from different signal areas can be visually correlated.
/// </summary>
public sealed record TimelineSlice(
    /// <summary>Event start time relative to trace start, in milliseconds.</summary>
    double StartMs,

    /// <summary>Event end time in milliseconds. Equals <see cref="StartMs"/> for point events.</summary>
    double EndMs,

    /// <summary>Signal area, e.g. "GC", "Contention", "HTTP", "Exception", "SQL", "Async".</summary>
    string Category,

    /// <summary>Human-readable description, e.g. "Gen2 Blocking 245 ms (Induced)".</summary>
    string Label,

    /// <summary>Severity determines the highlight colour in reports.</summary>
    FindingSeverity Severity)
{
    /// <summary>Duration in milliseconds (0 for point events).</summary>
    public double DurationMs => EndMs - StartMs;
}
