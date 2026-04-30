namespace DumpDetective.Core.Runtime;

/// <summary>Pre-distilled heap address and its inbound reference count.</summary>
internal readonly record struct HeapAddrCount(ulong Addr, int Count);

/// <summary>One bucket of the inbound-reference histogram: [Lo, Hi) with object count.</summary>
internal readonly record struct InboundBucket(int Lo, int Hi, int Count);

/// <summary>Aggregated string group stats: how many instances and total bytes for one distinct string value.</summary>
internal readonly record struct StringGroupStats(int Count, long TotalSize);
