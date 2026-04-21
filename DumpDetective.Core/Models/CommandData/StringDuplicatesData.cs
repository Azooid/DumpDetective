namespace DumpDetective.Core.Models.CommandData;

public sealed record StringDuplicatesData(
    IReadOnlyList<StringDupGroup> Groups,
    long TotalStrings,
    long TotalSize);

public sealed record StringDupGroup(string Value, int Count, long TotalSize);
