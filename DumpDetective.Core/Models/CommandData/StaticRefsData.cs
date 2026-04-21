namespace DumpDetective.Core.Models.CommandData;

public sealed record StaticRefsData(
    IReadOnlyList<StaticFieldEntry> Fields,
    int                             Total,
    long                            TotalSize);

public sealed record StaticFieldEntry(
    string  DeclType,
    string  FieldName,
    string  FieldType,
    bool    IsCollection,
    long    RetainedSize,
    ulong   Addr);
