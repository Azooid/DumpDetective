namespace DumpDetective.Core.Models.CommandData;

public sealed record HandleTableData(
    IReadOnlyDictionary<string, HandleKindInfo> ByKind,
    int                                         Total);

public sealed record HandleKindInfo(
    int                                          Count,
    long                                         TotalSize,
    IReadOnlyDictionary<string, HandleTypeStats> Types);

public sealed record HandleTypeStats(int Count, long Size);
