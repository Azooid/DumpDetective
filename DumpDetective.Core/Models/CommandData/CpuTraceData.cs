using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

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
    CpuStats?                       Stats = null,
    /// <summary>Semantic findings from pattern detectors. Empty when no patterns matched.</summary>
    IReadOnlyList<TraceFinding>?    SemanticFindings = null,
    /// <summary>Hot chains extracted from the call tree. Empty when TotalSamples == 0.</summary>
    IReadOnlyList<HotChain>?        HotChains = null,
    /// <summary>Per-category scores aggregated from SemanticFindings.</summary>
    IReadOnlyList<CategoryScore>?   CategoryScores = null,
    /// <summary>CPU samples bucketed per second (sorted chronologically). Used for timeline sparkline.</summary>
    IReadOnlyList<double>?          SamplesTimeline = null);

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

