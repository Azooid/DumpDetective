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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, (double TimeMs, string Frame)> PendingSchedule = new();
        internal readonly Dictionary<int, (double StartMs, string Frame)> PendingWaits = new();
        internal readonly List<AsyncTaskSummary> TaskSummaries = new();
        internal readonly List<(double WaitMs, string Frame)> WaitEvents = new();
        internal readonly Dictionary<string, int> ContinuationCounts = new(StringComparer.Ordinal);
        internal readonly List<double> ScheduleTimestamps = new();
        internal int ScheduledCount;
        internal int CompletedCount;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(evName, out byte kind))
                EvKind[evName] = kind = ComputeAsyncKind(evName);
            if (kind == 0) return;

            // ── Task/Scheduled ────────────────────────────────────────────────────────────────
            if (kind == 1) // Scheduled
            {
                ScheduledCount++;
                ScheduleTimestamps.Add(timestampMs);
                int taskId = GetIntPayload(ev, "TaskID", threadId);
                string frame = TopFrame(ev);
                PendingSchedule[taskId] = (timestampMs, frame);
                return;
            }

            // ── Task/Execute/Stop (completed) ─────────────────────────────
            if (kind == 2) // Completed
            {
                CompletedCount++;
                int taskId = GetIntPayload(ev, "TaskID", 0);
                if (taskId != 0 && PendingSchedule.TryGetValue(taskId, out var sched))
                {
                    double execMs = timestampMs - sched.TimeMs;
                    if (execMs > 0)
                        TaskSummaries.Add(new AsyncTaskSummary(taskId, execMs, sched.TimeMs, sched.Frame));
                    PendingSchedule.Remove(taskId);
                }
                return;
            }

            // ── Task/Wait/Begin — thread about to block synchronously ─────
            if (kind == 3) // WaitBegin
            {
                string frame = TopFrame(ev);
                PendingWaits[threadId] = (timestampMs, frame);
                return;
            }

            // ── Task/Wait/End ─────────────────────────────────────────────
            if (kind == 4) // WaitEnd
            {
                if (PendingWaits.TryGetValue(threadId, out var waitEntry))
                {
                    double waitMs = timestampMs - waitEntry.StartMs;
                    if (waitMs >= 0)
                        WaitEvents.Add((waitMs, waitEntry.Frame));
                    PendingWaits.Remove(threadId);
                }
                return;
            }

            // ── Awaiter/ScheduleContinuation ──────────────────────────────
            if (kind == 5) // Continuation
            {
                string frame = TopFrame(ev);
                ContinuationCounts.TryGetValue(frame, out int cnt);
                ContinuationCounts[frame] = cnt + 1;
            }
        }

        public bool WantsEvent(string eventName) { if (!EvKind.TryGetValue(eventName, out byte v)) EvKind[eventName] = v = ComputeAsyncKind(eventName); return v != 0; }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public AsyncTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                       string? processFilter = null)
    {
        var c = (Consumer)consumer;
        bool hasData = c.ScheduledCount > 0 || c.WaitEvents.Count > 0 || c.ContinuationCounts.Count > 0;
        if (!hasData)
        {
            return new AsyncTraceData(
                $"{traceFileName}  |  No async/Task events — collect with " +
                "--providers 'Microsoft-Windows-DotNETRuntime:0x40:5'",
                processFilter, 0, 0, 0, 0, 0, [], [], [], null, false);
        }

        // ── Aggregate sync-blocking hotspots ──────────────────────────────────
        var blockingAcc = new Dictionary<string, (int Count, double Total, double Max)>(StringComparer.Ordinal);
        foreach (var (waitMs, frame) in c.WaitEvents)
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
        var longestTasks = c.TaskSummaries
            .OrderByDescending(t => t.ExecutionMs)
            .Take(top)
            .ToList();

        double avgExec = c.TaskSummaries.Count > 0 ? c.TaskSummaries.Average(t => t.ExecutionMs) : 0;
        double maxExec = c.TaskSummaries.Count > 0 ? c.TaskSummaries.Max(t => t.ExecutionMs) : 0;

        // ── Top continuation sites ────────────────────────────────────────────
        var topContinuations = c.ContinuationCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new AsyncContinuationSite(kv.Key, kv.Value))
            .ToList();

        // ── Schedule-rate timeline ────────────────────────────────────────────
        IReadOnlyList<double>? rateTimeline = null;
        if (c.ScheduleTimestamps.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (double ts in c.ScheduleTimestamps)
            {
                int bucket = (int)(ts / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + 1;
            }
            int minB = int.MaxValue; int maxB = int.MinValue;
            foreach (int k in perSecond.Keys) { if (k < minB) minB = k; if (k > maxB) maxB = k; }
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value;
            rateTimeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.ScheduledCount:N0} tasks scheduled  •  {c.WaitEvents.Count:N0} sync-blocking waits";

        return new AsyncTraceData(
            info, processFilter,
            c.ScheduledCount, c.CompletedCount,
            c.WaitEvents.Count,
            avgExec, maxExec,
            syncHotspots, longestTasks, topContinuations,
            rateTimeline, HasData: true);
    }

    public AsyncTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new AsyncTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, [], [], [], null, false);
        }
    }
    // ─────────────────────────────────────────────────────────────────────────
    // Event name matchers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Classify event name once: 0=skip, 1=scheduled, 2=completed, 3=waitbegin, 4=waitend, 5=continuation.</summary>
    private static byte ComputeAsyncKind(string n)
    {
        if (IsTaskScheduled(n))      return 1;
        if (IsTaskCompleted(n))      return 2;
        if (IsTaskWaitBegin(n))      return 3;
        if (IsTaskWaitEnd(n))        return 4;
        if (IsAwaiterContinuation(n)) return 5;
        return 0;
    }

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


}
