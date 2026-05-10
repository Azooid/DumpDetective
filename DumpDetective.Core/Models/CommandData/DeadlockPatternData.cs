namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from heuristic deadlock pattern analysis.
/// Detects circular wait patterns from ContentionStart/Stop and WaitHandle events.
/// </summary>
public sealed record DeadlockPatternData(
    string TraceInfo,
    string? FilteredProcess,
    int    SuspectedDeadlockCount,
    int    LongWaitCount,
    double MaxWaitMs,
    double TotalWaitMs,
    IReadOnlyList<WaitChainEntry> WaitChains,
    bool HasData);

/// <summary>
/// A heuristic wait chain: two threads that were blocked waiting at overlapping times,
/// each at a different lock/synchronisation site.
/// </summary>
public sealed record WaitChainEntry(
    int    Thread1Id,
    int    Thread2Id,
    double Thread1StartMs,
    double Thread2StartMs,
    double OverlapMs,
    string Thread1Frame,
    string Thread2Frame);
