namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>HeapFragmentationAnalyzer</c>.</summary>
public sealed record HeapFragmentationData(
    IReadOnlyList<HeapSegmentInfo> Segments,
    IReadOnlyList<FreeHoleBucket> FreeDistribution);

public sealed record HeapSegmentInfo(
    string Kind,
    ulong  Address,
    long   CommittedBytes,
    long   LiveBytes,
    long   FreeBytes,
    int    PinnedCount);

public sealed record FreeHoleBucket(string Label, long Count, long TotalBytes, int SortKey);
