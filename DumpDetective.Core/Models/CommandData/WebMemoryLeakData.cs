using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

public sealed class WebMemoryLeakData
{
    public required string TraceInfo { get; init; }

    public required IReadOnlyList<double> HeapSeriesMb   { get; init; }
    public required IReadOnlyList<double> NodesSeries    { get; init; }
    public required IReadOnlyList<double> ListenerSeries { get; init; }

    public long MaxHeapBytes      { get; init; }
    public int  MaxNodes          { get; init; }
    public int  MaxListeners      { get; init; }
    public double ListenerNodeRatio { get; init; }

    /// <summary>Net change from first to last sample, as a fraction of the first sample (e.g. 0.5 = +50%).</summary>
    public double HeapGrowthFraction     { get; init; }
    public double NodesGrowthFraction    { get; init; }
    public double ListenerGrowthFraction { get; init; }

    public required IReadOnlyList<Finding> Findings { get; init; }
}
