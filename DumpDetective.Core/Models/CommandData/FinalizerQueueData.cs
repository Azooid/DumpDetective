namespace DumpDetective.Core.Models.CommandData;

public sealed record FinalizerQueueData(
    IReadOnlyDictionary<string, FinalizerTypeStats> Stats,
    int                                             Total,
    long                                            TotalSize,
    bool                                            FinalizerThreadBlocked,
    IReadOnlyList<string>                           FinalizerFrames,
    int                                             ResurrectionCount);

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
    IReadOnlyList<ulong>  Addresses);
