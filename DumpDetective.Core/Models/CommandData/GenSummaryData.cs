namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>GenSummaryAnalyzer</c>.</summary>
public sealed record GenSummaryData(
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LohBytes,
    long PohBytes,
    long FrozenBytes,
    long Gen0ObjCount,
    long Gen1ObjCount,
    long Gen2ObjCount,
    IReadOnlyList<SegmentRow> Segments,
    long FrozenObjCount,
    long FrozenObjSize,
    long PohObjCount,
    long PohObjSize,
    bool HeapWalkable);

/// <summary>One row in the segment details table.</summary>
public sealed record SegmentRow(string Address, string Kind, long CommittedBytes);
