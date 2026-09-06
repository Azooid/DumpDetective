using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebInputLatencyData
{
    public required string TraceInfo { get; init; }
    public int    SampleCount { get; init; }
    public long   MedianUs    { get; init; }
    public long   P95Us       { get; init; }
    public long   MaxUs       { get; init; }
    public required IReadOnlyList<long> WorstUs { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
