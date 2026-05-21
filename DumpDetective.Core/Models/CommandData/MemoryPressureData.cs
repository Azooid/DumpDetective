namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>MemoryPressureAnalyzer</c>.</summary>
public sealed record MemoryPressureData(
    /// <summary>Sum of all managed heap segment committed bytes.</summary>
    long ManagedHeapCommitted,
    /// <summary>Sum of managed heap segment reserved (not committed) bytes.</summary>
    long ManagedHeapReserved,
    /// <summary>Managed heap live (non-free) bytes.</summary>
    long ManagedHeapLive,
    /// <summary>Managed heap free/fragmented bytes.</summary>
    long ManagedHeapFree,
    /// <summary>Sum of all thread committed stack bytes (StackBase \u2212 StackLimit).</summary>
    long ThreadStacksCommitted,
    /// <summary>Thread count used to compute stack total.</summary>
    int  ThreadCount,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LohBytes,
    long PohBytes,
    int  SegmentCount,
    IReadOnlyList<MemoryRegionSummary> RegionSummary);

/// <summary>Summary of heap segments grouped by kind.</summary>
public sealed record MemoryRegionSummary(
    string Kind,
    int    Count,
    long   CommittedBytes,
    long   ReservedBytes,
    long   LiveBytes,
    long   FreeBytes);
