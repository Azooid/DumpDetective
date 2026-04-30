using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Trace.Analyzers;

/// <summary>
/// Parses ContentionStart/Stop events from a .nettrace / .etl / .etl.zip trace.
/// Identifies lock contention hotspots by call site and wait duration.
/// </summary>
public sealed class ContentionTraceAnalyzer
{
    public ContentionTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new ContentionTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, 0, [], []);
        }
    }

    public ContentionTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        var pending  = new Dictionary<int, double>();
        var events   = new List<ContentionEvent>();
        var hotspots = new Dictionary<string, HotspotAcc>(StringComparer.Ordinal);

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                if (evName.EndsWith("Contention/Start", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ContentionStart",  StringComparison.OrdinalIgnoreCase))
                {
                    pending[ev.ThreadID] = ev.TimeStampRelativeMSec;
                    continue;
                }

                if (evName.EndsWith("Contention/Stop", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ContentionStop",  StringComparison.OrdinalIgnoreCase))
                {
                    if (!pending.TryGetValue(ev.ThreadID, out double startMs)) continue;
                    pending.Remove(ev.ThreadID);

                    double waitMs  = ev.TimeStampRelativeMSec - startMs;
                    string frame   = TopFrame(ev);

                    events.Add(new ContentionEvent(ev.ThreadID, waitMs, startMs, frame));

                    if (!hotspots.TryGetValue(frame, out var acc))
                        hotspots[frame] = acc = new HotspotAcc();
                    acc.Count++;
                    acc.TotalMs += waitMs;
                    if (waitMs > acc.MaxMs) acc.MaxMs = waitMs;
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            return new ContentionTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, 0, [], []);
        }

        if (events.Count == 0)
            return new ContentionTraceData(
                $"{traceFileName}  |  0 contention events — collect with --providers Microsoft-Windows-DotNETRuntime:0x4000:4",
                processFilter, 0, 0, 0, 0, 0, [], []);

        double totalWait    = events.Sum(e => e.WaitMs);
        double maxWait      = events.Max(e => e.WaitMs);
        double avgWait      = totalWait / events.Count;
        int    threadsHit   = events.Select(e => e.ThreadId).Distinct().Count();

        var topHotspots = hotspots
            .OrderByDescending(kv => kv.Value.TotalMs)
            .Take(top)
            .Select(kv => new ContentionHotspot(kv.Key, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs))
            .ToList();

        var recentEvents = events
            .OrderByDescending(e => e.WaitMs)
            .Take(top)
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {events.Count:N0} contentions  •  {totalWait:F1} ms total wait";

        return new ContentionTraceData(info, processFilter,
            events.Count, totalWait, maxWait, avgWait, threadsHit,
            topHotspots, recentEvents);
    }

    private static string TopFrame(TraceEvent ev)
    {
        var cs = ev.CallStack();
        if (cs is null) return "(no stack)";
        // Skip runtime internals (Monitor, clr.dll) to surface user code
        var cur = cs;
        while (cur is not null)
        {
            string? name = cur.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name) &&
                !name.Contains("Monitor",          StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("JIT_MonEnter",     StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("clr!",             StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("ntdll",            StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("KERNELBASE",       StringComparison.OrdinalIgnoreCase))
                return name;
            cur = cur.Caller;
        }
        // Fall back to module name if no resolved user frame found
        var first = cs;
        while (first is not null)
        {
            string mod = first.CodeAddress.ModuleName ?? "";
            if (mod.Length > 0) return mod;
            first = first.Caller;
        }
        return "(no stack)";
    }

    private sealed class HotspotAcc
    {
        public int    Count;
        public double TotalMs;
        public double MaxMs;
    }
}
