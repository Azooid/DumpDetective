namespace DumpDetective.Core.Models.CommandData;

public sealed record LargeObjectsData(
    IReadOnlyList<LargeObjectEntry> Objects,
    long                            TotalSize,
    IReadOnlyList<LargeSegmentInfo> Segments);

public sealed record LargeObjectEntry(
    string  Type,
    string  ElemType,
    long    Size,
    string  Segment,
    ulong   Addr);

public sealed record LargeSegmentInfo(
    string  Kind,
    long    Used,
    long    Reserved,
    int     ObjectCount);
