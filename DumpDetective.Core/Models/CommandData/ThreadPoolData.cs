namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ThreadPoolAnalyzer</c>.</summary>
public sealed record ThreadPoolData(
    int?                        MinThreads,
    int?                        MaxThreads,
    int?                        ActiveWorkers,
    int?                        IdleWorkers,
    bool                        InfoAvailable,
    IReadOnlyDictionary<string, int> TaskStateCounts,
    IReadOnlyDictionary<string, int> WorkItems);
