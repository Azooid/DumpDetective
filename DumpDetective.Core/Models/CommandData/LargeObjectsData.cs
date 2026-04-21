namespace DumpDetective.Core.Models.CommandData;

public sealed record LargeObjectsData(
    IReadOnlyList<LargeObjectEntry> Objects,
    long                            TotalSize,
    IReadOnlyList<LargeSegmentInfo> Segments,
    long                            LohCommitted = 0,
    long                            LohLive      = 0,
    long                            LohFree      = 0,
    long                            MinSize      = 85_000);

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
