namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from Large Object Heap (LOH) trace analysis.
/// Dedicated trend view extracted from GCHeapStats.GenerationSize3 events.
/// </summary>
public sealed record LohTraceData(
    string TraceInfo,
    string? FilteredProcess,
    long   StartLohBytes,
    long   PeakLohBytes,
    long   EndLohBytes,
    long   LohGrowthBytes,
    int    TotalGcCount,
    int    Gen2GcsWithLohGrowth,
    bool   IsTrendingUp,
    IReadOnlyList<double>? LohSizeTimeline,
    bool HasData);
