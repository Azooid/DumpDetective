namespace DumpDetective.Core.Models.CommandData;

public sealed record StaticRefsData(
    IReadOnlyList<StaticFieldEntry> Fields,
    int                             Total,
    long                            TotalSize,
    /// <summary>True when retained sizes were estimated via sampling rather than full BFS.</summary>
    bool                            IsEstimated        = false,
    /// <summary>Number of modules skipped due to corrupt or inconsistent PE metadata.</summary>
    int                             SkippedModuleCount = 0,
    /// <summary>Non-reference (value-type) static fields; null when not collected.</summary>
    IReadOnlyList<NonRefStaticFieldEntry>? NonRefFields = null);

public sealed record StaticFieldEntry(
    string  DeclType,
    string  FieldName,
    string  FieldType,
    bool    IsCollection,
    long    RetainedSize,
    ulong   Addr,
    /// <summary>True when this field's retained size was extrapolated from a sample.</summary>
    bool    IsEstimated = false);

/// <summary>A non-reference (value-type) static field entry.</summary>
public sealed record NonRefStaticFieldEntry(
    string  DeclType,
    string  FieldName,
    string  FieldType,
    /// <summary>"Primitive", "Enum", "Struct", or "Pointer"</summary>
    string  ElementKind,
    /// <summary>Readable value for primitive and enum fields; null for structs/pointers.</summary>
    string? Value = null);
