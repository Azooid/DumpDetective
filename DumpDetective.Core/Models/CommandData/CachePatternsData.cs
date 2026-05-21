namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>CachePatternsAnalyzer</c>.</summary>
public sealed record CachePatternsData(
    IReadOnlyList<CachePatternEntry> Entries,
    int                              TotalInstances,
    long                             TotalEntryCount,
    long                             TotalSize);

/// <summary>One cache-like collection type with aggregate entry counts.</summary>
public sealed record CachePatternEntry(
    string TypeName,
    int    InstanceCount,
    long   TotalEntryCount,
    long   TotalSize,
    long   AverageEntries,
    long   MaxEntries,
    /// <summary>True when at least one instance exceeds the unbounded-growth threshold.</summary>
    bool   HasOversizedInstance,
    string CollectionKind,
    /// <summary>BFS-retained bytes for all instances of this type. 0 when BFS index is unavailable.</summary>
    long   RetainedSize = 0,
    /// <summary>True when <see cref="RetainedSize"/> was scaled from a sample rather than measured exhaustively.</summary>
    bool   RetainedIsEstimated = false);
