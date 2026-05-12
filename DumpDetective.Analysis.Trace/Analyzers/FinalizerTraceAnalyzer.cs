using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses GC/FinalizeObject and GC suspension events to detect finalizer backlog,
/// queue growth, and long finalizer bursts that delay managed execution resumption.
/// </summary>
public sealed class FinalizerTraceAnalyzer
{
    public FinalizerTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new FinalizerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, [], [], null, false);
        }
    }

    public FinalizerTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                       string? processFilter = null, Action<string>? progress = null)
    {
        // Track finalizer events per type
        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Track GC suspension windows to detect long finalizer periods
        double? suspendStart = null;
        int gcIndex = 0;
        var bursts = new List<FinalizerBurstEntry>();
        var pendingBurst = new Dictionary<int, (double StartMs, int Count, string TopType)>();

        // Per-second finalizer event count for sparkline
        var perSecond = new Dictionary<int, int>();

        int totalEvents = 0;
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
                    progress($"{totalEvents:N0} finalizer events  \u2022  {byType.Count} types");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // Track GC suspends — the time between SuspendEEStart and RestartEEStop
                // is the STW window in which the finalizer thread runs.
                if (evName.EndsWith("GC/SuspendEEStart", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("SuspendEEStart",    StringComparison.OrdinalIgnoreCase))
                {
                    suspendStart = ev.TimeStampRelativeMSec;
                    gcIndex++;
                    continue;
                }

                if ((evName.EndsWith("GC/RestartEEStop", StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("RestartEEStop",    StringComparison.OrdinalIgnoreCase)) &&
                    suspendStart.HasValue)
                {
                    double burstMs = ev.TimeStampRelativeMSec - suspendStart.Value;
                    if (pendingBurst.TryGetValue(gcIndex, out var pb))
                    {
                        bursts.Add(new FinalizerBurstEntry(gcIndex, suspendStart.Value,
                            pb.Count, burstMs, pb.TopType));
                        pendingBurst.Remove(gcIndex);
                    }
                    suspendStart = null;
                    continue;
                }

                // GC/FinalizeObject — payload: TypeName
                if (!evName.EndsWith("FinalizeObject", StringComparison.OrdinalIgnoreCase)) continue;

                string typeName = SafeStr(ev, "TypeName");
                if (typeName.Length == 0) typeName = SafeStr(ev, "Type");
                if (typeName.Length == 0) typeName = "(unknown)";

                totalEvents++;

                byType.TryGetValue(typeName, out int prev);
                byType[typeName] = prev + 1;

                // Associate with current burst window
                if (!pendingBurst.TryGetValue(gcIndex, out var cur))
                    pendingBurst[gcIndex] = (ev.TimeStampRelativeMSec, 1, typeName);
                else
                    pendingBurst[gcIndex] = (cur.StartMs, cur.Count + 1,
                        cur.Count < byType.GetValueOrDefault(cur.TopType) ? typeName : cur.TopType);

                // Per-second bucket for sparkline
                int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                perSecond.TryGetValue(bucket, out int pv);
                perSecond[bucket] = pv + 1;
            }
        }
        catch (Exception ex)
        {
            return new FinalizerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, [], [], null, false);
        }

        if (totalEvents == 0)
        {
            return new FinalizerTraceData(
                $"{traceFileName}  |  0 FinalizeObject events — collect with --providers Microsoft-Windows-DotNETRuntime:0x1:5",
                processFilter, 0, 0, 0, 0, false, [], [], null, false);
        }

        var topTypes = byType
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new FinalizerTypeSummary(kv.Key, kv.Value,
                totalEvents > 0 ? kv.Value * 100.0 / totalEvents : 0))
            .ToList();

        var topBursts = bursts
            .OrderByDescending(b => b.FinalizerCount)
            .Take(top)
            .ToList();

        double maxBurstMs = bursts.Count > 0 ? bursts.Max(b => b.BurstDurationMs) : 0;
        double avgBurstMs = bursts.Count > 0 ? bursts.Average(b => b.BurstDurationMs) : 0;

        // Detect growing queue: last third of bursts has more events than first third
        bool isGrowing = false;
        if (bursts.Count >= 6)
        {
            int third = bursts.Count / 3;
            double firstAvg = bursts.Take(third).Average(b => b.FinalizerCount);
            double lastAvg  = bursts.Skip(bursts.Count - third).Average(b => b.FinalizerCount);
            isGrowing = lastAvg > firstAvg * 1.5;
        }

        // Build sparkline
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
                      $"  |  {totalEvents:N0} finalization events  •  {byType.Count} types";

        return new FinalizerTraceData(info, processFilter,
            totalEvents, bursts.Count, maxBurstMs, avgBurstMs, isGrowing,
            topTypes, topBursts, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }
}
