namespace DumpDetective.Core.Models.CommandData;

public sealed record TypeInstancesData(
    IReadOnlyDictionary<string, TypeMatchStats> ByType,
    long                                        TotalCount,
    long                                        TotalSize,
    string                                      SearchTerm,
    bool                                        HasRetained = false);

public sealed record TypeMatchStats(
    long                          Count,
    long                          TotalSize,
    int                           Gen0,
    int                           Gen1,
    int                           Gen2,
    int                           Loh,
    int                           Poh,
    long                          MaxSingle,
    IReadOnlyList<InstanceEntry>  LargestInstances,
    long                          TotalRetainedSize = 0);

public sealed record InstanceEntry(ulong Addr, long Size, string Gen, long RetainedSize = 0);
