namespace DumpDetective.Core.Models.CommandData;

public sealed record StringDuplicatesData(
    IReadOnlyList<StringDupGroup> Groups,
    long TotalStrings,
    long TotalSize,
    /// <summary>Duplicate byte[] groups (populated with --include-arrays; null otherwise).</summary>
    IReadOnlyList<ByteArrayDupGroup>? ByteArrayGroups = null,
    /// <summary>Encoding waste samples for the top string groups (populated with --include-encoding; null otherwise).</summary>
    IReadOnlyList<StringEncodingGroup>? EncodingGroups = null);

public sealed record StringDupGroup(string Value, int Count, long TotalSize);

/// <summary>A group of byte[] arrays with identical content.</summary>
public sealed record ByteArrayDupGroup(
    int  Count,
    long TotalSize,
    int  ArrayLength,
    /// <summary>Hex preview of the first 16 bytes.</summary>
    string Preview);

/// <summary>Encoding analysis for a top string group.</summary>
public sealed record StringEncodingGroup(
    string Value,
    int    Count,
    long   TotalSize,
    /// <summary>True when all characters are in the ASCII range (0–7F).</summary>
    bool   IsAllAscii,
    /// <summary>True when all characters fit in Latin-1 (0⃿FF).</summary>
    bool   IsAllLatin1,
    /// <summary>Waste in bytes if encoded as UTF-8 vs. .NET UTF-16 storage.</summary>
    long   Utf8WasteBytes);
