namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from GC handle leak trace analysis.
/// Populated from GCHandle/Created and GCHandle/Destroyed events (keyword 0x4000).
/// </summary>
public sealed record HandleLeakTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalCreated,
    int    TotalDestroyed,
    int    NetGrowth,
    bool   IsGrowing,
    IReadOnlyList<HandleKindSummary> TypeBreakdown,
    IReadOnlyList<double>?           GrowthTimeline,
    bool HasData);

/// <summary>Handle creation/destruction counts per handle kind.</summary>
public sealed record HandleKindSummary(
    string HandleKind,
    int    Created,
    int    Destroyed,
    int    NetGrowth);
