namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>GcRootMapAnalyzer</c>.</summary>
public sealed record GcRootMapData(
    IReadOnlyList<RootKindSummary>   ByKind,
    IReadOnlyList<RootTypeEntry>     TopTypesByKind,
    int                              TotalHandles,
    int                              TotalStackRoots,
    long                             TotalHandleMemory,
    bool                             StackRootsPartial = false);

/// <summary>Aggregate count and memory held by one GC root kind.</summary>
public sealed record RootKindSummary(
    string KindName,
    int    HandleCount,
    int    StackRootCount,
    long   EstimatedMemory);

/// <summary>A type frequently appearing as a GC root target.</summary>
public sealed record RootTypeEntry(
    string KindName,
    string TypeName,
    int    Count,
    long   TotalSize);
