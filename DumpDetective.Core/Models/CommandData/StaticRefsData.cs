namespace DumpDetective.Core.Models.CommandData;

public sealed record StaticRefsData(
    IReadOnlyList<StaticFieldEntry> Fields,
    int                             Total,
    long                            TotalSize,
    /// <summary>True when retained sizes were estimated via sampling rather than full BFS.</summary>
    bool                            IsEstimated = false);

public sealed record StaticFieldEntry(
    string  DeclType,
    string  FieldName,
    string  FieldType,
    bool    IsCollection,
    long    RetainedSize,
    ulong   Addr,
    /// <summary>True when this field's retained size was extrapolated from a sample.</summary>
    bool    IsEstimated = false);
