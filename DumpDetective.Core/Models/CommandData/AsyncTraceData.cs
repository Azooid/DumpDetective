namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from async/Task trace analysis.
/// Detects sync-over-async patterns, long-running continuations, and Task scheduling pressure.
/// </summary>
public sealed record AsyncTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalTasksScheduled,
    int TotalTasksCompleted,
    int SyncBlockingOccurrences,
    double AvgExecutionMs,
    double MaxExecutionMs,
    IReadOnlyList<AsyncBlockingSite>  SyncBlockingHotspots,
    IReadOnlyList<AsyncTaskSummary>   LongestTasks,
    IReadOnlyList<AsyncContinuationSite> TopContinuationSites,
    /// <summary>Tasks scheduled per second (sorted chronologically). Used for timeline sparkline.</summary>
    IReadOnlyList<double>?            ScheduleRateTimeline,
    bool HasData);

/// <summary>A call site where a thread blocked synchronously on a Task (.Wait()/.Result).</summary>
public sealed record AsyncBlockingSite(
    string Frame,
    int    Count,
    double TotalBlockMs,
    double MaxBlockMs);

/// <summary>A task entry with measured execution time.</summary>
public sealed record AsyncTaskSummary(
    int    TaskId,
    double ExecutionMs,
    double ScheduledTimeMs,
    string TopFrame);

/// <summary>A frame that schedules continuations frequently (high async throughput or storms).</summary>
public sealed record AsyncContinuationSite(
    string Frame,
    int    Count);
