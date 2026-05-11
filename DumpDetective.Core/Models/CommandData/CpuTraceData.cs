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
    IReadOnlyList<double>?          SamplesTimeline = null,
    /// <summary>
    /// Number of CPU samples that contained at least one frame which could not be resolved to a
    /// method name. Covers two cases: (a) managed frames where CLR rundown events were absent
    /// (TraceLog returns "ManagedModule" with no FullMethodName); (b) completely unresolved frames
    /// where no module name is available at all (PerfView shows these as &lt;&lt;?!?&gt;&gt;).
    /// A high value relative to <see cref="TotalSamples"/> means the call tree attribution is
    /// incomplete. Re-capture the trace with CLR provider and rundown events enabled.
    /// </summary>
    int                             UnresolvedSamples = 0);

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

