namespace DumpDetective.Core.Models.CommandData;

public sealed record HeapStatsData(
    IReadOnlyList<HeapStatRow> Types,
    long TotalSize,
    long TotalObjs);

public sealed record HeapStatRow(string Name, long Count, long Size, string Gen, ulong MethodTable = 0,
    long Gen2Count = 0, long Gen2Size = 0);
