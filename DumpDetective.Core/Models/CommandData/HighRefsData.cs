namespace DumpDetective.Core.Models.CommandData;

public sealed record HighRefsData(
    IReadOnlyList<HighRefEntry> Candidates,
    long TotalObjs,
    long TotalRefs,
    int  UniqueReferencedAddrs);

public sealed record HighRefEntry(
    string                          Type,
    ulong                           Addr,
    long                            OwnSize,
    long                            RetainedSize,
    string                          Gen,
    int                             InboundRefs,
    int                             DistinctSourceTypes,
    IReadOnlyList<(string Type, int Count)> TopSources);
