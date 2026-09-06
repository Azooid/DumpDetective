using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebGcPauseRow
{
    public long   TimestampUs { get; init; }
    public long   DurationUs  { get; init; }
    public required string Kind { get; init; }
}

public sealed class WebGcPressureData
{
    public required string TraceInfo { get; init; }
    public required IReadOnlyList<WebGcPauseRow> Pauses { get; init; }
    public int  MajorCount        { get; init; }
    public int  MinorCount        { get; init; }
    public long TotalPauseUs      { get; init; }
    public long LongestPauseUs    { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
