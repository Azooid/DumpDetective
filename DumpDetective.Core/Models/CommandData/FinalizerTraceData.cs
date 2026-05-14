namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from GC finalizer trace analysis.
/// Populated from GC/FinalizeObject, GC/SuspendEEStart, and GC/RestartEEStop events.
/// </summary>
public sealed record FinalizerTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalFinalizationEvents,
    int    GcCountWithFinalizers,
    double MaxFinalizerBurstMs,
    double AvgFinalizerBurstMs,
    bool   IsQueueGrowing,
    IReadOnlyList<FinalizerTypeSummary>  TopFinalizerTypes,
    IReadOnlyList<FinalizerBurstEntry>   FinalizerBursts,
    IReadOnlyList<double>?               BurstTimeline,
    bool HasData);

/// <summary>Aggregate finalizer activity per type.</summary>
public sealed record FinalizerTypeSummary(
    string TypeName,
    int    Count,
    double PctOfTotal);

/// <summary>A burst of finalizer activity observed during a GC suspension window.</summary>
public sealed record FinalizerBurstEntry(
    int    GcIndex,
    double SuspendStartMs,
    int    FinalizerCount,
    double BurstDurationMs,
    string TopType);
