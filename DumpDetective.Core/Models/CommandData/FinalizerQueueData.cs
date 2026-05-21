namespace DumpDetective.Core.Models.CommandData;

public sealed record FinalizerQueueData(
    IReadOnlyDictionary<string, FinalizerTypeStats> Stats,
    int                                             Total,
    long                                            TotalSize,
    bool                                            FinalizerThreadBlocked,
    IReadOnlyList<string>                           FinalizerFrames,
    int                                             ResurrectionCount,
    int                                             FinalizerThreadId = 0,
    uint                                            FinalizerThreadOSId = 0);

public sealed record FinalizerTypeStats(
    int                   Count,
    long                  Size,
    int                   Gen0,
    int                   Gen1,
    int                   Gen2,
    int                   Loh,
    int                   Poh,
    bool                  HasDispose,
    bool                  IsCritical,
    IReadOnlyList<ulong>  Addresses,
    /// <summary>
    /// True when at least one sampled instance has a dispose-flag field (e.g. _disposed, m_disposed)
    /// that reads as <see langword="false"/>, indicating Dispose() was never called.
    /// </summary>
    bool                  SuspectedUndisposed = false);
