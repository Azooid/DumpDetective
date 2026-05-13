using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public TaskSchedulerTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                           string? processFilter = null, Action<string>? progress = null)
    {
        // pending: TaskId → (scheduledMs, frame)
        var pendingTasks = new Dictionary<int, (double ScheduledMs, string Frame)>();
        // pending waits: ThreadID → startMs
        var pendingWaits = new Dictionary<int, double>();

        int scheduled = 0, completed = 0, cancelled = 0;
        var longRunning = new List<LongRunningTask>();
        double maxDuration = 0, totalDuration = 0;
        double maxWait = 0;
        var perSecond = new Dictionary<int, int>();
        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;
        var evKind = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ev in trace.Events)
            {
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{scheduled:N0} scheduled  \u2022  {completed:N0} done");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                if (!evKind.TryGetValue(evName, out byte kind))
                    evKind[evName] = kind = ComputeTaskSchedulerKind(evName);
                if (kind == 0) continue;

                // Task Scheduled
                if (kind == 1)
                {
                    scheduled++;
                    int taskId = SafeInt(ev, "TaskID");
                    if (taskId == 0) taskId = ev.ThreadID ^ (int)(ev.TimeStampRelativeMSec);
                    string frame = TopUserFrame(ev);
                    pendingTasks[taskId] = (ev.TimeStampRelativeMSec, frame);

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out int pv);
                    perSecond[bucket] = pv + 1;
                    continue;
                }

                // Task Completed
                if (kind == 2)
                {
                    int taskId = SafeInt(ev, "TaskID");
                    bool isCancelled = SafeStr(ev, "IsExceptional") == "True" ||
                                       evName.Contains("Cancel", StringComparison.OrdinalIgnoreCase);
                    if (isCancelled) cancelled++;
                    else completed++;

                    if (taskId != 0 && pendingTasks.TryGetValue(taskId, out var pending))
                    {
                        double duration = ev.TimeStampRelativeMSec - pending.ScheduledMs;
                        pendingTasks.Remove(taskId);
                        totalDuration += duration;
                        if (duration > maxDuration) maxDuration = duration;
                        if (duration >= SlowTaskMs)
                            longRunning.Add(new LongRunningTask(taskId, ev.ThreadID,
                                pending.ScheduledMs, duration, pending.Frame));
                    }
                    continue;
                }

                // Task WaitBegin
                if (kind == 3)
                {
                    pendingWaits[ev.ThreadID] = ev.TimeStampRelativeMSec;
                    continue;
                }

                // Task WaitEnd
                if (kind == 4)
                {
                    if (pendingWaits.TryGetValue(ev.ThreadID, out double waitStart))
                    {
                        double waitMs = ev.TimeStampRelativeMSec - waitStart;
                        if (waitMs > maxWait) maxWait = waitMs;
                        pendingWaits.Remove(ev.ThreadID);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new TaskSchedulerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, 0, [], null, false);
        }

        if (scheduled == 0)
        {
            return new TaskSchedulerTraceData(
                $"{traceFileName}  |  0 Task events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x40:5' (TplKeyword)",
                processFilter, 0, 0, 0, 0, 0, 0, 0, [], null, false);
        }

        int totalFinished = completed + cancelled;
        double avg = totalFinished > 0 ? totalDuration / totalFinished : 0;

        var topLong = longRunning.OrderByDescending(t => t.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {scheduled:N0} tasks  •  {longRunning.Count} slow (>{SlowTaskMs/1000:F0}s)";

        return new TaskSchedulerTraceData(info, processFilter,
            scheduled, completed, cancelled, longRunning.Count,
            maxDuration, avg, maxWait, topLong, timeline, HasData: true);
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

    private static int SafeInt(TraceEvent ev, string field)
    {
        try { return (int)Convert.ChangeType(ev.PayloadByName(field), typeof(int)); } catch { return 0; }
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
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
