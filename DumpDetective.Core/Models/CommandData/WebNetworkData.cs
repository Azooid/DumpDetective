using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebNetworkRow
{
    public required string Url          { get; init; }
    public string?         Method       { get; init; }
    public string?         ResourceType { get; init; }
    public long DurationUs         { get; init; }
    public long TimeToFirstByteUs  { get; init; }
    public long EncodedBytes       { get; init; }
    public bool Failed             { get; init; }
    public bool FromCache          { get; init; }
}

public sealed class WebNetworkData
{
    public required string TraceInfo { get; init; }
    public required IReadOnlyList<WebNetworkRow> Requests { get; init; }
    public int  TotalRequests { get; init; }
    public int  FailedCount   { get; init; }
    public long TotalBytes    { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
