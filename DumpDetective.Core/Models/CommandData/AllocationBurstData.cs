namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from allocation burst trace analysis.
/// Detects short-duration allocation spikes rather than aggregate totals.
/// Populated from GCAllocationTick events bucketed into 500 ms windows.
/// </summary>
public sealed record AllocationBurstData(
    string TraceInfo,
    string? FilteredProcess,
    int    BurstCount,
    double PeakBurstRateKbPerSec,
    double AvgAllocationRateKbPerSec,
    IReadOnlyList<AllocationBurstEntry> BurstPeriods,
    IReadOnlyList<double>?              RateTimeline,
    bool HasData);

/// <summary>A detected burst period with allocation rate above the burst threshold.</summary>
public sealed record AllocationBurstEntry(
    double StartMs,
    double EndMs,
    double PeakRateKbPerSec,
    long   EstimatedBytes,
    string TopType);
