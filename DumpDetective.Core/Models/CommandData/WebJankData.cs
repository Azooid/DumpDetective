using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebJankData
{
    public required string TraceInfo { get; init; }
    public int    BeginFrameCount   { get; init; }
    public int    DroppedFrameCount { get; init; }
    public double DropRatePct       { get; init; }
    public double EffectiveFps      { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
}
