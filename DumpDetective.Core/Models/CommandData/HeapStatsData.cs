namespace DumpDetective.Core.Models.CommandData;

public sealed record HeapStatsData(
    IReadOnlyList<HeapStatRow> Types,
    long TotalSize,
    long TotalObjs);

public sealed record HeapStatRow(string Name, long Count, long Size, string Gen);
