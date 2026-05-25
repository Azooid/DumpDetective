using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses TPL/Task Scheduled, Started, Completed, WaitBegin and WaitEnd events.
/// Detects long-running tasks, cancelled tasks, and continuation-chain flooding.
/// </summary>
public sealed class TaskSchedulerTraceAnalyzer
{
    private const double SlowTaskMs = 5_000; // 5 s = long-running task

    public TaskSchedulerTraceData Analyze(string tracePath, int top = 20,
                                           string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new TaskSchedulerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, 0, [], null, false);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, (double ScheduledMs, string Frame)> PendingTasks = new();
        internal readonly Dictionary<int, double> PendingWaits = new();
        internal int Scheduled, Completed, Cancelled;
        internal readonly List<LongRunningTask> LongRunning = new();
        internal double MaxDuration, TotalDuration, MaxWait;
        internal readonly Dictionary<int, int> PerSecond = new();

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind = ComputeTaskSchedulerKind(meta.EventName);
            if (kind == 0) return;

            if (kind == 1)
            {
                Scheduled++;
                int taskId = SafeInt(ev, "TaskID");
                if (taskId == 0) taskId = threadId ^ (int)(timestampMs);
                string frame = TopUserFrame(ev);
                PendingTasks[taskId] = (timestampMs, frame);

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out int pv);
                PerSecond[bucket] = pv + 1;
                return;
            }

            if (kind == 2)
            {
                int taskId = SafeInt(ev, "TaskID");
                bool isCancelled = SafeStr(ev, "IsExceptional") == "True" ||
                                   meta.EventName.Contains("Cancel", StringComparison.OrdinalIgnoreCase);
                if (isCancelled) Cancelled++;
                else Completed++;

                if (taskId != 0 && PendingTasks.TryGetValue(taskId, out var pending))
                {
                    double duration = timestampMs - pending.ScheduledMs;
                    PendingTasks.Remove(taskId);
                    TotalDuration += duration;
                    if (duration > MaxDuration) MaxDuration = duration;
                    if (duration >= SlowTaskMs)
                        LongRunning.Add(new LongRunningTask(taskId, threadId,
                            pending.ScheduledMs, duration, pending.Frame));
                }
                return;
            }

            if (kind == 3)
            {
                PendingWaits[threadId] = timestampMs;
                return;
            }

            if (kind == 4)
            {
                if (PendingWaits.TryGetValue(threadId, out double waitStart))
                {
                    double waitMs = timestampMs - waitStart;
                    if (waitMs > MaxWait) MaxWait = waitMs;
                    PendingWaits.Remove(threadId);
                }
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == TaskScheduled => 1,
                    _ when meta.Kind == TaskCompleted => 2,
                    _ when meta.Kind == TaskWaitBegin => 3,
                    _ when meta.Kind == TaskWaitEnd => 4,
                    _ when meta.IsKnown => 0,
                    _ => ComputeTaskSchedulerKind(meta.EventName)
                };
            return v != 0;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public TaskSchedulerTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                               string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Scheduled == 0)
        {
            return new TaskSchedulerTraceData(
                $"{traceFileName}  |  0 Task events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x40:5' (TplKeyword)",
                processFilter, 0, 0, 0, 0, 0, 0, 0, [], null, false);
        }

        int totalFinished = c.Completed + c.Cancelled;
        double avg = totalFinished > 0 ? c.TotalDuration / totalFinished : 0;

        var topLong = c.LongRunning.OrderByDescending(t => t.DurationMs).Take(top).ToList();

        var timeline = BuildTimeline(c.PerSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Scheduled:N0} tasks  •  {c.LongRunning.Count} slow (>{SlowTaskMs/1000:F0}s)";

        return new TaskSchedulerTraceData(info, processFilter,
            c.Scheduled, c.Completed, c.Cancelled, c.LongRunning.Count,
            c.MaxDuration, avg, c.MaxWait, topLong, timeline, HasData: true);
    }

    public TaskSchedulerTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                           string? processFilter = null, Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return new TaskSchedulerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, 0, [], null, false);
        }
    }

    /// <summary>Classify event name once: 0=skip, 1=scheduled, 2=completed, 3=waitbegin, 4=waitend.</summary>
    private static byte ComputeTaskSchedulerKind(string n)
    {
        if (n.Contains("Task/Scheduled",   StringComparison.OrdinalIgnoreCase) ||
            n.Contains("TaskScheduled",     StringComparison.OrdinalIgnoreCase)) return 1;
        if (n.Contains("Task/Completed",   StringComparison.OrdinalIgnoreCase) ||
            n.Contains("TaskCompleted",     StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Task/Execute/Stop", StringComparison.OrdinalIgnoreCase)) return 2;
        if (n.Contains("TaskWaitBegin",    StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Task/Wait/Begin",   StringComparison.OrdinalIgnoreCase)) return 3;
        if (n.Contains("TaskWaitEnd",      StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Task/Wait/End",     StringComparison.OrdinalIgnoreCase)) return 4;
        return 0;
    }


    private static string TopUserFrame(TraceEvent ev)
    {
        var cs = ev.CallStack();
        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name) &&
                !name.StartsWith("System.Threading.Tasks", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("clr!",    StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("ntdll",   StringComparison.OrdinalIgnoreCase))
                return name;
            cur = cur.Caller;
        }
        return "(no stack)";
    }
}
