namespace DumpDetective.Core.Models.CommandData;

public sealed record GcRootsData(
    string                                              TypeName,
    IReadOnlyList<GcRootTarget>                         Targets,
    bool                                                Capped,
    IReadOnlyDictionary<ulong, IReadOnlyList<GcRootInfo>>  DirectRoots,
    IReadOnlyDictionary<ulong, IReadOnlyList<ReferrerInfo>> Referrers);

public sealed record GcRootTarget(ulong Addr, string Type, long Size, string Gen);

public sealed record GcRootInfo(string KindLabel, ulong RootAddress, int? ThreadId);

public sealed record ReferrerInfo(ulong Addr, string Type);
