using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Analyzers;

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
        CommandBase.RunStatus($"Parsing trace: {Path.GetFileName(tracePath)}...", () => { });

        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath, new TraceLogOptions { ConversionLog = TextWriter.Null });
            var events       = new List<WaitEventSummary>();
            var adjustments  = new List<TpAdjustmentRecord>();
            var eventCounts  = new Dictionary<string, int>(StringComparer.Ordinal);
            int starvCount   = 0;
            uint tpMax = 0, tpFinal = 0;
            int totalEvents = 0;

            foreach (var ev in trace.Events)
            {
                totalEvents++;
                string evName = ev.EventName ?? "?";
                if (!eventCounts.TryGetValue(evName, out int cnt)) cnt = 0;
                eventCounts[evName] = cnt + 1;

                if (evName.Contains("WaitHandleWaitStart", StringComparison.OrdinalIgnoreCase))
                {
                    int src = TryGetInt(ev, "WaitSource");
                    events.Add(new WaitEventSummary(
                        ThreadId:       ev.ThreadID,
                        WaitSourceName: WaitSourceNames.GetValueOrDefault(src, $"Unknown({src})"),
                        TopFrames:      []));
                }
                else if (evName.Contains("Adjustment", StringComparison.OrdinalIgnoreCase))
                {
                    int    reason     = TryGetInt(ev, "Reason");
                    uint   newCount   = (uint)TryGetInt(ev, "NewWorkerThreadCount");
                    double avgThrough = TryGetDouble(ev, "AverageThroughput");
                    string rName      = AdjustmentReasonNames.GetValueOrDefault(reason, $"#{reason}");
                    if (rName == "Starvation") starvCount++;
                    if (newCount > tpMax) tpMax = newCount;
                    tpFinal = newCount;
                    adjustments.Add(new TpAdjustmentRecord(ev.TimeStamp.ToString("HH:mm:ss.fff"),
                        newCount, rName, avgThrough));
                }
            }

            // Group WaitHandle events by thread/source, top N
            var groupedEvents = events
                .GroupBy(e => (e.ThreadId, e.WaitSourceName))
                .OrderByDescending(g => g.Count())
                .Take(top)
                .Select(g => new WaitEventSummary(g.Key.ThreadId, g.Key.WaitSourceName, []))
                .ToList();

            string info = $"{Path.GetFileName(tracePath)}  |  events: {totalEvents:N0}";
            return new ThreadPoolStarvationData(info, totalEvents, groupedEvents,
                adjustments.TakeLast(50).ToList(), starvCount, tpMax, tpFinal, eventCounts);
        }
        catch (Exception ex)
        {
            return new ThreadPoolStarvationData(
                $"Failed to parse trace: {ex.Message}", 0, [], [], 0, 0, 0,
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
