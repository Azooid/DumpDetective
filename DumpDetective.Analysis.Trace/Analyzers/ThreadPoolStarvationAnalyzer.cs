using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses a .nettrace file for WaitHandleWait events and ThreadPool hill-climbing
/// adjustments, then returns structured data for the starvation report.
/// </summary>
public sealed class ThreadPoolStarvationAnalyzer
{
    private static readonly Dictionary<int, string> WaitSourceNames = new()
    {
        [0] = "Unknown",     [1] = "MonitorWait", [2] = "MonitorEnter",
        [3] = "WaitOne",     [4] = "WaitAny",     [5] = "WaitAll",
    };

    private static readonly Dictionary<int, string> AdjustmentReasonNames = new()
    {
        [0] = "Warmup",     [1] = "Initializing",  [2] = "RandomMove",
        [3] = "ClimbingMove",[4] = "ChangePoint",  [5] = "Stabilizing",
        [6] = "Starvation", [7] = "ThreadTimedOut", [8] = "CooperativeBlocking",
    };

    public ThreadPoolStarvationData Analyze(string tracePath, int top = 10)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath, new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top);
        }
        catch (Exception ex)
        {
            return new ThreadPoolStarvationData($"Failed: {ex.Message}", 0, [], [], 0, 0, 0,
                new Dictionary<string, int>());
        }
    }

    private sealed class Consumer() : ITraceEventConsumer
    {
        internal readonly List<WaitEventSummary> Events = new();
        internal readonly List<TpAdjustmentRecord> Adjustments = new();
        internal readonly Dictionary<string, int> EventCounts = new(StringComparer.Ordinal);
        internal int StarvCount;
        internal uint TpMax, TpFinal;

        public void Consume(Microsoft.Diagnostics.Tracing.TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (!EventCounts.TryGetValue(meta.EventName, out int cnt)) cnt = 0;
            EventCounts[meta.EventName] = cnt + 1;

            if (meta.EventName.Contains("WaitHandleWaitStart", StringComparison.OrdinalIgnoreCase))
            {
                int src = TryGetInt(ev, "WaitSource");
                Events.Add(new WaitEventSummary(
                    ThreadId:       threadId,
                    WaitSourceName: WaitSourceNames.GetValueOrDefault(src, $"Unknown({src})"),
                    TopFrames:      []));
            }
            else if (meta.EventName.Contains("Adjustment", StringComparison.OrdinalIgnoreCase))
            {
                int    reason     = TryGetInt(ev, "Reason");
                uint   newCount   = (uint)TryGetInt(ev, "NewWorkerThreadCount");
                double avgThrough = TryGetDouble(ev, "AverageThroughput");
                string rName      = AdjustmentReasonNames.GetValueOrDefault(reason, $"#{reason}");
                if (rName == "Starvation") StarvCount++;
                if (newCount > TpMax) TpMax = newCount;
                TpFinal = newCount;
                Adjustments.Add(new TpAdjustmentRecord(ev.TimeStamp.ToString("HH:mm:ss.fff"),
                    newCount, rName, avgThrough));
            }
        }

        public bool WantsEvent(in TraceEventMeta meta) => meta.Kind switch
        {
            _ when meta.Kind == WaitHandleWaitStart || meta.Kind == ThreadPoolAdjustment => true,
            _ when meta.IsKnown => false,
            _ => meta.EventName.Contains("WaitHandleWaitStart", StringComparison.OrdinalIgnoreCase) ||
                 meta.EventName.Contains("Adjustment",          StringComparison.OrdinalIgnoreCase)
        };

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer() => new Consumer();

    public ThreadPoolStarvationData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 10)
    {
        var c = (Consumer)consumer;
        int totalEvents = c.EventCounts.Values.Sum();

        var groupedEvents = c.Events
            .GroupBy(e => (e.ThreadId, e.WaitSourceName))
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g => new WaitEventSummary(g.Key.ThreadId, g.Key.WaitSourceName, []))
            .ToList();

        string info = $"{traceFileName}  |  events: {totalEvents:N0}";
        return new ThreadPoolStarvationData(info, totalEvents, groupedEvents,
            c.Adjustments.TakeLast(50).ToList(), c.StarvCount, c.TpMax, c.TpFinal, c.EventCounts);
    }

    public ThreadPoolStarvationData Analyze(TraceLog trace, string traceFileName, int top = 10,
                                             Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer();
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top);
        }
        catch (Exception ex)
        {
            return new ThreadPoolStarvationData($"Failed: {ex.Message}", 0, [], [], 0, 0, 0,
                new Dictionary<string, int>());
        }
    }

    private static int TryGetInt(Microsoft.Diagnostics.Tracing.TraceEvent ev, string field)
    {
        try { return (int)ev.PayloadByName(field); } catch { return 0; }
    }

    private static double TryGetDouble(Microsoft.Diagnostics.Tracing.TraceEvent ev, string field)
    {
        try { return (double)ev.PayloadByName(field); } catch { return 0; }
    }
}
