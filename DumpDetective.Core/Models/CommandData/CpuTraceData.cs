namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Top-level result from CPU sampling trace analysis.
/// </summary>
public sealed record CpuTraceData(
    string                          TraceInfo,
    int                             TotalSamples,
    double                          SamplingIntervalMs,
    string?                         FilteredProcess,
    IReadOnlyList<CpuMethodStats>   TopMethods,
    IReadOnlyList<CpuCallNode>      HotPath,
    IReadOnlyList<CpuCallNode>      CallTree,
    CpuStats?                       Stats = null);

/// <summary>
/// Aggregate CPU utilisation statistics derived from the sample stream.
/// All percentages are 0–100 and represent CPU utilisation across all logical cores
/// (i.e. a value >100 is possible when more than one core is active).
/// </summary>
public sealed record CpuStats(
    double TraceDurationMs,
    double AvgCpuPct,
    double MaxCpuPct,
    double TotalCpuMs,
    int    ActiveThreads,
    int    LogicalCores,
    string TopProcessName);

/// <summary>Flat per-method CPU stats (for the top-N table).</summary>
public sealed record CpuMethodStats(
    string Method,
    string Module,
    int    ExclusiveSamples,
    int    InclusiveSamples,
    double ExclusivePct,
    double InclusivePct);

/// <summary>
/// Node in the inclusive call tree.
/// Children are sorted descending by <see cref="InclusiveSamples"/>.
/// </summary>
public sealed record CpuCallNode(
    string                      Method,
    string                      Module,
    int                         InclusiveSamples,
    int                         ExclusiveSamples,
    double                      InclusivePct,
    double                      ExclusivePct,
    IReadOnlyList<CpuCallNode>  Children);
