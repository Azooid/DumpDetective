namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from Task Scheduler / TPL trace analysis.
/// Populated from Task Scheduled/Started/Completed/WaitBegin/WaitEnd events.
/// </summary>
public sealed record TaskSchedulerTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalScheduled,
    int    TotalCompleted,
    int    TotalCancelled,
    int    LongRunningTaskCount,
    double MaxTaskDurationMs,
    double AvgTaskDurationMs,
    double MaxWaitMs,
    IReadOnlyList<LongRunningTask> LongRunningTasks,
    IReadOnlyList<double>?         ScheduledTimeline,
    bool HasData);

/// <summary>A Task that took longer than the slow threshold to complete.</summary>
public sealed record LongRunningTask(
    int    TaskId,
    int    ThreadId,
    double ScheduledMs,
    double DurationMs,
    string TopFrame);
