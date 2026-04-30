namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Allocation data from GCAllocationTick events.
/// Each tick fires every ~100 KB of allocations — totals are sampled estimates.
/// </summary>
public sealed record AllocTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalTicks,
    long EstimatedTotalBytes,
    IReadOnlyList<AllocTypeSummary> TopTypes,
    IReadOnlyList<AllocCallSiteSummary> TopCallSites);

public sealed record AllocTypeSummary(
    string TypeName,
    int Ticks,
    long EstimatedBytes,
    double PctOfTotal);

public sealed record AllocCallSiteSummary(
    string TopFrame,
    string TypeName,
    int Ticks,
    long EstimatedBytes);
