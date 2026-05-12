using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses ContentionStart/Stop events from a .nettrace / .etl trace.
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
                                        string? processFilter = null, Action<string>? progress = null)
    {
        // Key: ThreadID → (startTimeMs, topFrame captured at ContentionStart)
        var pending  = new Dictionary<int, (double StartMs, string Frame)>();
        var events   = new List<ContentionEvent>();
        var hotspots = new Dictionary<string, HotspotAcc>(StringComparer.Ordinal);
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
                    progress($"{events.Count:N0} contentions");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                if (evName.EndsWith("Contention/Start", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ContentionStart",  StringComparison.OrdinalIgnoreCase))
                {
                    // Capture the call stack NOW — it is on the Start event, not Stop.
                    pending[ev.ThreadID] = (ev.TimeStampRelativeMSec, TopFrame(ev));
                    continue;
                }

                if (evName.EndsWith("Contention/Stop", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ContentionStop",  StringComparison.OrdinalIgnoreCase))
                {
                    if (!pending.TryGetValue(ev.ThreadID, out var entry)) continue;
                    pending.Remove(ev.ThreadID);

                    double waitMs = ev.TimeStampRelativeMSec - entry.StartMs;
                    string frame  = entry.Frame;

                    events.Add(new ContentionEvent(ev.ThreadID, waitMs, entry.StartMs, frame));

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

        // ── Contention wait-time timeline ──────────────────────────────────────
        // Bucket total wait-ms per second for a sparkline.
        IReadOnlyList<double> waitTimeline = [];
        if (events.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (var ev in events)
            {
                int bucket = (int)(ev.TimeMs / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + ev.WaitMs;
            }
            int minB = perSecond.Keys.Min();
            int maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond)
                tl[kv.Key - minB] = kv.Value;
            waitTimeline = tl;
        }

        return new ContentionTraceData(info, processFilter,
            events.Count, totalWait, maxWait, avgWait, threadsHit,
            topHotspots, recentEvents, waitTimeline);
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
