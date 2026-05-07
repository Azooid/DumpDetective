namespace DumpDetective.Core.Models.CommandData;

public sealed record ContentionTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalContentions,
    double TotalWaitMs,
    double MaxWaitMs,
    double AvgWaitMs,
    int ThreadsAffected,
    IReadOnlyList<ContentionHotspot> Hotspots,
    IReadOnlyList<ContentionEvent> Events,
    /// <summary>Total contention wait-ms bucketed per second (sorted). Used for sparkline.</summary>
    IReadOnlyList<double>? WaitTimeline = null);

public sealed record ContentionHotspot(
    string Location,
    int Count,
    double TotalWaitMs,
    double MaxWaitMs);

public sealed record ContentionEvent(
    int ThreadId,
    double WaitMs,
    double TimeMs,
    string TopFrame);
