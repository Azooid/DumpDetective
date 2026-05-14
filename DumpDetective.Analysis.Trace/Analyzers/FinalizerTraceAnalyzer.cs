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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, int> ByType = new(StringComparer.OrdinalIgnoreCase);
        internal double? SuspendStart;
        internal int GcIndex;
        internal readonly List<FinalizerBurstEntry> Bursts = new();
        internal readonly Dictionary<int, (double StartMs, int Count, string TopType)> PendingBurst = new();
        internal readonly Dictionary<int, int> PerSecond = new();
        internal int TotalEvents;

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(evName, out byte kind))
                EvKind[evName] = kind =
                    evName.EndsWith("GC/SuspendEEStart", StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("SuspendEEStart",    StringComparison.OrdinalIgnoreCase) ? (byte)1 :
                    evName.EndsWith("GC/RestartEEStop",  StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("RestartEEStop",     StringComparison.OrdinalIgnoreCase) ? (byte)2 :
                    evName.EndsWith("FinalizeObject",    StringComparison.OrdinalIgnoreCase) ? (byte)3 :
                    (byte)0;
            if (kind == 0) return;

            if (kind == 1)
            {
                SuspendStart = timestampMs;
                GcIndex++;
                return;
            }

            if (kind == 2 && SuspendStart.HasValue)
            {
                double burstMs = timestampMs - SuspendStart.Value;
                if (PendingBurst.TryGetValue(GcIndex, out var pb))
                {
                    Bursts.Add(new FinalizerBurstEntry(GcIndex, SuspendStart.Value,
                        pb.Count, burstMs, pb.TopType));
                    PendingBurst.Remove(GcIndex);
                }
                SuspendStart = null;
                return;
            }

            // kind == 3: FinalizeObject
            string typeName = SafeStr(ev, "TypeName");
            if (typeName.Length == 0) typeName = SafeStr(ev, "Type");
            if (typeName.Length == 0) typeName = "(unknown)";

            TotalEvents++;

            ByType.TryGetValue(typeName, out int prev);
            ByType[typeName] = prev + 1;

            if (!PendingBurst.TryGetValue(GcIndex, out var cur))
                PendingBurst[GcIndex] = (timestampMs, 1, typeName);
            else
                PendingBurst[GcIndex] = (cur.StartMs, cur.Count + 1,
                    cur.Count < ByType.GetValueOrDefault(cur.TopType) ? typeName : cur.TopType);

            int bucket = (int)(timestampMs / 1000.0);
            PerSecond.TryGetValue(bucket, out int pv);
            PerSecond[bucket] = pv + 1;
        }

        public bool WantsEvent(string eventName) { if (!EvKind.TryGetValue(eventName, out byte v)) { v = eventName.EndsWith("GC/SuspendEEStart", StringComparison.OrdinalIgnoreCase) || eventName.EndsWith("SuspendEEStart", StringComparison.OrdinalIgnoreCase) ? (byte)1 : eventName.EndsWith("GC/RestartEEStop", StringComparison.OrdinalIgnoreCase) || eventName.EndsWith("RestartEEStop", StringComparison.OrdinalIgnoreCase) ? (byte)2 : eventName.EndsWith("FinalizeObject", StringComparison.OrdinalIgnoreCase) ? (byte)3 : (byte)0; EvKind[eventName] = v; } return v != 0; }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public FinalizerTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                           string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.TotalEvents == 0)
        {
            return new FinalizerTraceData(
                $"{traceFileName}  |  0 FinalizeObject events — collect with --providers Microsoft-Windows-DotNETRuntime:0x1:5",
                processFilter, 0, 0, 0, 0, false, [], [], null, false);
        }

        var topTypes = c.ByType
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => new FinalizerTypeSummary(kv.Key, kv.Value,
                c.TotalEvents > 0 ? kv.Value * 100.0 / c.TotalEvents : 0))
            .ToList();

        var topBursts = c.Bursts
            .OrderByDescending(b => b.FinalizerCount)
            .Take(top)
            .ToList();

        double maxBurstMs = c.Bursts.Count > 0 ? c.Bursts.Max(b => b.BurstDurationMs) : 0;
        double avgBurstMs = c.Bursts.Count > 0 ? c.Bursts.Average(b => b.BurstDurationMs) : 0;

        bool isGrowing = false;
        if (c.Bursts.Count >= 6)
        {
            int third = c.Bursts.Count / 3;
            double firstAvg = c.Bursts.Take(third).Average(b => b.FinalizerCount);
            double lastAvg  = c.Bursts.Skip(c.Bursts.Count - third).Average(b => b.FinalizerCount);
            isGrowing = lastAvg > firstAvg * 1.5;
        }

        var timeline = BuildTimeline(c.PerSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.TotalEvents:N0} finalization events  •  {c.ByType.Count} types";

        return new FinalizerTraceData(info, processFilter,
            c.TotalEvents, c.Bursts.Count, maxBurstMs, avgBurstMs, isGrowing,
            topTypes, topBursts, timeline, HasData: true);
    }

    public FinalizerTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new FinalizerTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, false, [], [], null, false);
        }
    }

}
