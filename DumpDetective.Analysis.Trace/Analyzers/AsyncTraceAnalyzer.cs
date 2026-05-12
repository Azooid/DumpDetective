using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses TPL Task lifecycle events from a .nettrace / .etl trace.
///
/// Events consumed:
///   Task/Scheduled (TaskScheduled)   — a new Task entered the scheduler queue
///   Task/Execute/Start (TaskStarted) — a Task began execution on a thread
///   Task/Execute/Stop (TaskCompleted)— a Task finished execution
///   Task/Wait/Begin  (TaskWaitBegin) — a thread is about to synchronously block on a Task (.Wait()/.Result())
///   Task/Wait/End    (TaskWaitEnd)   — the blocking wait returned
///   Awaiter/ScheduleContinuation     — an async continuation was queued
///
/// Required provider flags:
///   Microsoft-Windows-DotNETRuntime: keyword 0x40 (Tasks) at level 5
///   dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x40:5'
/// </summary>
public sealed class AsyncTraceAnalyzer
{
    public AsyncTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new AsyncTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], [], null, false);
        }
    }

    public AsyncTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                  string? processFilter = null, Action<string>? progress = null)
    {
        // Task execution tracking: TaskID → scheduled time
        var pendingSchedule = new Dictionary<int, (double TimeMs, string Frame)>();
        // Task wait tracking (sync-over-async): ThreadID → wait start time + frame
        var pendingWaits = new Dictionary<int, (double StartMs, string Frame)>();

        var taskSummaries       = new List<AsyncTaskSummary>();
        var waitEvents          = new List<(double WaitMs, string Frame)>();
        var continuationCounts  = new Dictionary<string, int>(StringComparer.Ordinal);
        var scheduleTimestamps  = new List<double>();

        int scheduledCount  = 0;
        int completedCount  = 0;
        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{scheduledCount:N0} tasks  \u2022  {completedCount:N0} done");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // ── Task/Scheduled ────────────────────────────────────────────
                if (IsTaskScheduled(evName))
                {
                    scheduledCount++;
                    scheduleTimestamps.Add(ev.TimeStampRelativeMSec);
                    int taskId = GetIntPayload(ev, "TaskID", ev.ThreadID);
                    string frame = TopFrame(ev);
                    pendingSchedule[taskId] = (ev.TimeStampRelativeMSec, frame);
                    continue;
                }

                // ── Task/Execute/Stop (completed) ─────────────────────────────
                if (IsTaskCompleted(evName))
                {
                    completedCount++;
                    int taskId = GetIntPayload(ev, "TaskID", 0);
                    if (taskId != 0 && pendingSchedule.TryGetValue(taskId, out var sched))
                    {
                        double execMs = ev.TimeStampRelativeMSec - sched.TimeMs;
                        if (execMs > 0)
                            taskSummaries.Add(new AsyncTaskSummary(taskId, execMs, sched.TimeMs, sched.Frame));
                        pendingSchedule.Remove(taskId);
                    }
                    continue;
                }

                // ── Task/Wait/Begin — thread about to block synchronously ─────
                if (IsTaskWaitBegin(evName))
                {
                    string frame = TopFrame(ev);
                    pendingWaits[ev.ThreadID] = (ev.TimeStampRelativeMSec, frame);
                    continue;
                }

                // ── Task/Wait/End ─────────────────────────────────────────────
                if (IsTaskWaitEnd(evName))
                {
                    if (pendingWaits.TryGetValue(ev.ThreadID, out var waitEntry))
                    {
                        double waitMs = ev.TimeStampRelativeMSec - waitEntry.StartMs;
                        if (waitMs >= 0)
                            waitEvents.Add((waitMs, waitEntry.Frame));
                        pendingWaits.Remove(ev.ThreadID);
                    }
                    continue;
                }

                // ── Awaiter/ScheduleContinuation ──────────────────────────────
                if (IsAwaiterContinuation(evName))
                {
                    string frame = TopFrame(ev);
                    continuationCounts.TryGetValue(frame, out int cnt);
                    continuationCounts[frame] = cnt + 1;
                }
            }
        }
        catch (Exception ex)
        {
            return new AsyncTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], [], null, false);
        }

        bool hasData = scheduledCount > 0 || waitEvents.Count > 0 || continuationCounts.Count > 0;
        if (!hasData)
        {
            return new AsyncTraceData(
                $"{traceFileName}  |  No async/Task events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x40:5'",
                processFilter, 0, 0, 0, 0, 0, [], [], [], null, false);
        }

        // ── Aggregate sync-blocking hotspots ──────────────────────────────────
        var blockingAcc = new Dictionary<string, (int Count, double Total, double Max)>(StringComparer.Ordinal);
        foreach (var (waitMs, frame) in waitEvents)
        {
            blockingAcc.TryGetValue(frame, out var acc);
            blockingAcc[frame] = (acc.Count + 1, acc.Total + waitMs, Math.Max(acc.Max, waitMs));
        }
        var syncHotspots = blockingAcc
            .OrderByDescending(kv => kv.Value.Total)
            .Take(top)
            .Select(kv => new AsyncBlockingSite(kv.Key, kv.Value.Count, kv.Value.Total, kv.Value.Max))
            .ToList();

        // ── Longest tasks ─────────────────────────────────────────────────────
        var longestTasks = taskSummaries
            .OrderByDescending(t => t.ExecutionMs)
            .Take(top)
            .ToList();

        double avgExec = taskSummaries.Count > 0 ? taskSummaries.Average(t => t.ExecutionMs) : 0;
        double maxExec = taskSummaries.Count > 0 ? taskSummaries.Max(t => t.ExecutionMs) : 0;

        // ── Top continuation sites ────────────────────────────────────────────
        var topContinuations = continuationCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new AsyncContinuationSite(kv.Key, kv.Value))
            .ToList();

        // ── Schedule-rate timeline ────────────────────────────────────────────
        IReadOnlyList<double>? rateTimeline = null;
        if (scheduleTimestamps.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (double ts in scheduleTimestamps)
            {
                int bucket = (int)(ts / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + 1;
            }
            int minB = 0, maxB = 0;
            foreach (int k in perSecond.Keys) { if (k < minB || minB == 0) minB = k; if (k > maxB) maxB = k; }
            // Recalculate properly
            minB = int.MaxValue; maxB = int.MinValue;
            foreach (int k in perSecond.Keys) { if (k < minB) minB = k; if (k > maxB) maxB = k; }
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            rateTimeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {scheduledCount:N0} tasks scheduled  •  {waitEvents.Count:N0} sync-blocking waits";

        return new AsyncTraceData(
            info, processFilter,
            scheduledCount, completedCount,
            waitEvents.Count,
            avgExec, maxExec,
            syncHotspots, longestTasks, topContinuations,
            rateTimeline, HasData: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Event name matchers
    // ─────────────────────────────────────────────────────────────────────────
    private static bool IsTaskScheduled(string n) =>
        n.Contains("Task/Scheduled",   StringComparison.OrdinalIgnoreCase) ||
        n.Contains("TaskScheduled",     StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskCompleted(string n) =>
        n.Contains("Task/Execute/Stop", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("TaskCompleted",      StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Task/Completed",     StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskWaitBegin(string n) =>
        n.Contains("Task/Wait/Begin", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("TaskWaitBegin",   StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskWaitEnd(string n) =>
        n.Contains("Task/Wait/End", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("TaskWaitEnd",   StringComparison.OrdinalIgnoreCase);

    private static bool IsAwaiterContinuation(string n) =>
        n.Contains("Awaiter/ScheduleContinuation", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("ScheduleContinuation",          StringComparison.OrdinalIgnoreCase) ||
        n.Contains("ContinuationScheduled",         StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers shared with other analyzers
    // ─────────────────────────────────────────────────────────────────────────
    private static string TopFrame(TraceEvent ev)
    {
        var cs = ev.CallStack();
        if (cs is null) return "(no stack)";
        var cur = cs;
        while (cur is not null)
        {
            string name = cur.CodeAddress?.FullMethodName ?? "";
            if (name.Length > 0 && !IsRuntimeFrame(name))
                return Truncate(name, 120);
            cur = cur.Caller;
        }
        return cs.CodeAddress?.ModuleName ?? "(no stack)";
    }

    private static bool IsRuntimeFrame(string name) =>
        name.StartsWith("System.Runtime.",          StringComparison.Ordinal) ||
        name.StartsWith("System.Threading.Tasks.",  StringComparison.Ordinal) ||
        name.StartsWith("System.Threading.Thread.", StringComparison.Ordinal) ||
        name.Contains("clr!",  StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ntdll", StringComparison.OrdinalIgnoreCase);

    private static int GetIntPayload(TraceEvent ev, string field, int fallback)
    {
        try { return (int)(ev.PayloadByName(field) ?? fallback); }
        catch { return fallback; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
