namespace DumpDetective.Core.Models.CommandData;

public sealed record HighRefsData(
    IReadOnlyList<HighRefEntry> Candidates,
    long TotalObjs,
    long TotalRefs,
    int  UniqueReferencedAddrs,
    IReadOnlyList<(string Label, int Count)> RefHistogram);

public sealed record HighRefEntry(
    string                          Type,
    ulong                           Addr,
    long                            OwnSize,
    long                            RetainedSize,
    string                          Gen,
    int                             InboundRefs,
    int                             DistinctSourceTypes,
    IReadOnlyList<(string Type, int Count)> TopSources);
