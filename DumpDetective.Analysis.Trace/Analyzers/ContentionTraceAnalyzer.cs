using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<int, (double StartMs, string Frame)> Pending = new();
        internal readonly List<ContentionEvent> Events = new();
        internal readonly Dictionary<string, HotspotAcc> Hotspots = new(StringComparer.Ordinal);

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind =
                    meta.EventName.EndsWith("Contention/Start", StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("ContentionStart",  StringComparison.OrdinalIgnoreCase) ? (byte)1 :
                    meta.EventName.EndsWith("Contention/Stop",  StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("ContentionStop",   StringComparison.OrdinalIgnoreCase) ? (byte)2 :
                    (byte)0;
            if (kind == 0) return;

            if (kind == 1)
            {
                // Capture the call stack NOW — it is on the Start event, not Stop.
                Pending[threadId] = (timestampMs, TopFrame(ev));
                return;
            }

            if (kind == 2)
            {
                if (!Pending.TryGetValue(threadId, out var entry)) return;
                Pending.Remove(threadId);

                double waitMs = timestampMs - entry.StartMs;
                string frame  = entry.Frame;

                Events.Add(new ContentionEvent(threadId, waitMs, entry.StartMs, frame));

                if (!Hotspots.TryGetValue(frame, out var acc))
                    Hotspots[frame] = acc = new HotspotAcc();
                acc.Count++;
                acc.TotalMs += waitMs;
                if (waitMs > acc.MaxMs) acc.MaxMs = waitMs;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == ContentionStart => 1,
                    _ when meta.Kind == ContentionStop => 2,
                    _ when meta.IsKnown => 0,
                    _ => meta.EventName.EndsWith("Contention/Start",  StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("ContentionStart",   StringComparison.OrdinalIgnoreCase) ? (byte)1
                       : meta.EventName.EndsWith("Contention/Stop",   StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("ContentionStop",    StringComparison.OrdinalIgnoreCase) ? (byte)2
                       : (byte)0
                };
            return v != 0;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public ContentionTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                            string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Events.Count == 0)
            return new ContentionTraceData(
                $"{traceFileName}  |  0 contention events — collect with --providers Microsoft-Windows-DotNETRuntime:0x4000:4",
                processFilter, 0, 0, 0, 0, 0, [], []);

        double totalWait    = c.Events.Sum(e => e.WaitMs);
        double maxWait      = c.Events.Max(e => e.WaitMs);
        double avgWait      = totalWait / c.Events.Count;
        int    threadsHit   = c.Events.Select(e => e.ThreadId).Distinct().Count();

        var topHotspots = c.Hotspots
            .OrderByDescending(kv => kv.Value.TotalMs)
            .Take(top)
            .Select(kv => new ContentionHotspot(kv.Key, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs))
            .ToList();

        var recentEvents = c.Events
            .OrderByDescending(e => e.WaitMs)
            .Take(top)
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Events.Count:N0} contentions  •  {totalWait:F1} ms total wait";

        // ── Contention wait-time timeline ──────────────────────────────────────
        IReadOnlyList<double> waitTimeline = [];
        if (c.Events.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (var ev in c.Events)
            {
                int bucket = (int)(ev.TimeMs / 1000.0);
                perSecond.TryGetValue(bucket, out double prev);
                perSecond[bucket] = prev + ev.WaitMs;
            }
            waitTimeline = BuildTimeline(perSecond) ?? [];
        }

        return new ContentionTraceData(info, processFilter,
            c.Events.Count, totalWait, maxWait, avgWait, threadsHit,
            topHotspots, recentEvents, waitTimeline);
    }

    public ContentionTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new ContentionTraceData($"Failed: {ex.Message}", processFilter, 0, 0, 0, 0, 0, [], []);
        }
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
