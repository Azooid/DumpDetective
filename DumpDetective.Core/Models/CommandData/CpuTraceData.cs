using DumpDetective.Core.Models;

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
    IReadOnlyList<CallTreeNode>     HotPath,
    IReadOnlyList<CallTreeNode>     CallTree,
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

