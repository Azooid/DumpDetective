using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses Windows Kernel Process/Start and Process/Stop events to detect
/// process restarts, crashes, and abnormal exits.
/// </summary>
public sealed class ProcessLifecycleAnalyzer
{
    private const double RestartWindowMs = 30_000; // 30 s: stop→start = restart

    public ProcessLifecycleData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new ProcessLifecycleData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], false);
        }
    }

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly List<ProcessEvent> Events = new();
        internal readonly Dictionary<string, double> LastStop = new(StringComparer.OrdinalIgnoreCase);
        internal int Restarts, Abnormal;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            bool isStart =
                evName.EndsWith("Process/Start",  StringComparison.OrdinalIgnoreCase) ||
                evName.EndsWith("ProcessStart",   StringComparison.OrdinalIgnoreCase) ||
                evName.Contains("ProcessStart/Start", StringComparison.OrdinalIgnoreCase);
            bool isStop =
                evName.EndsWith("Process/Stop",   StringComparison.OrdinalIgnoreCase) ||
                evName.EndsWith("ProcessStop",    StringComparison.OrdinalIgnoreCase) ||
                evName.Contains("ProcessStop/Stop",  StringComparison.OrdinalIgnoreCase);

            if (!isStart && !isStop) return;

            string procName = SafeStr(ev, "ImageFileName");
            if (procName.Length == 0) procName = processName;
            if (procName.Length == 0) procName = "(unknown)";

            if (_processFilter is not null &&
                !procName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            int? exitCode = null;
            if (isStop)
            {
                try { exitCode = (int)Convert.ChangeType(ev.PayloadByName("ExitCode"), typeof(int)); } catch { }
            }

            string eventType = isStart ? "Start" : "Stop";
            Events.Add(new ProcessEvent(procName, ev.ProcessID, eventType,
                timestampMs, exitCode));

            if (isStart && LastStop.TryGetValue(procName, out double stopMs))
            {
                if (timestampMs - stopMs < RestartWindowMs)
                    Restarts++;
                LastStop.Remove(procName);
            }

            if (isStop)
            {
                LastStop[procName] = timestampMs;
                if (exitCode.HasValue && exitCode != 0)
                    Abnormal++;
            }
        }

        public bool WantsEvent(string eventName) => eventName.Contains("Process", StringComparison.OrdinalIgnoreCase);

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public ProcessLifecycleData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                             string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Events.Count == 0)
        {
            return new ProcessLifecycleData(
                $"{traceFileName}  |  0 process lifecycle events — collect with " +
                "--providers 'Microsoft-Windows-Kernel-Process:0x10:4' or use PerfView with Kernel Process events",
                processFilter, 0, 0, 0, 0, [], [], false);
        }

        int totalStarts = c.Events.Count(e => e.EventType == "Start");
        int totalStops  = c.Events.Count(e => e.EventType == "Stop");

        var groups = c.Events
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g =>
            {
                int s  = g.Count(e => e.EventType == "Start");
                int st = g.Count(e => e.EventType == "Stop");
                int ab = g.Count(e => e.EventType == "Stop" && e.ExitCode.HasValue && e.ExitCode != 0);
                int r  = Math.Max(0, Math.Min(s, st) - (ab > 0 ? 0 : 0));
                return new ProcessGroupSummary(g.Key, s, st, r, ab);
            })
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  filter: {processFilter}" : "") +
                      $"  |  {totalStarts} starts  •  {totalStops} stops" +
                      (c.Restarts > 0 ? $"  •  {c.Restarts} restarts" : "") +
                      (c.Abnormal > 0 ? $"  •  {c.Abnormal} abnormal exits" : "");

        return new ProcessLifecycleData(info, processFilter,
            totalStarts, totalStops, c.Restarts, c.Abnormal,
            c.Events.OrderBy(e => e.TimeMs).ToList(), groups, HasData: true);
    }

    public ProcessLifecycleData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new ProcessLifecycleData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], [], false);
        }
    }

}
