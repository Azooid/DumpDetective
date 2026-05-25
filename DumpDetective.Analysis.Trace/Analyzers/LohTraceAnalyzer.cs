using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Dedicated Large Object Heap (LOH) trend analyzer.
/// Extracts GenerationSize3 from GCHeapStats events to detect LOH growth,
/// fragmentation pressure, and monotonically increasing trends.
/// </summary>
public sealed class LohTraceAnalyzer
{
    public LohTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new LohTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, false, null, false);
        }
    }

    private static readonly Dictionary<string, bool> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly List<(double TimeMs, long Bytes)> LohSizes = new();
        internal int Gen2WithGrowth;
        internal long PrevLoh = -1;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out bool isHeapStats))
                EvKind[meta.EventName] = isHeapStats =
                    meta.EventName.EndsWith("GCHeapStats",  StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.EndsWith("GC/HeapStats", StringComparison.OrdinalIgnoreCase) ||
                    meta.EventName.Contains("HeapStats",    StringComparison.OrdinalIgnoreCase);
            if (!isHeapStats) return;

            long loh = SafeLong(ev, "GenerationSize3");
            if (loh <= 0) loh = SafeLong(ev, "LohSize");
            if (loh <= 0) loh = SafeLong(ev, "Gen3Size");
            if (loh <= 0) return;

            LohSizes.Add((timestampMs, loh));

            if (PrevLoh > 0 && loh > PrevLoh)
                Gen2WithGrowth++;
            PrevLoh = loh;
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out bool v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == GCHeapStats => true,
                    _ when meta.IsKnown => false,
                    _ => meta.EventName.EndsWith("GCHeapStats",  StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.EndsWith("GC/HeapStats", StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.Contains("HeapStats",    StringComparison.OrdinalIgnoreCase)
                };
            return v;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public LohTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                     string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.LohSizes.Count == 0)
        {
            return new LohTraceData(
                $"{traceFileName}  |  0 GCHeapStats events — collect with " +
                "--profile gc-verbose or --providers 'Microsoft-Windows-DotNETRuntime:0x1:4'",
                processFilter, 0, 0, 0, 0, 0, 0, false, null, false);
        }

        long startLoh = c.LohSizes[0].Bytes;
        long peakLoh  = c.LohSizes.Max(x => x.Bytes);
        long endLoh   = c.LohSizes[^1].Bytes;
        long growth   = peakLoh - startLoh;

        bool isTrendingUp = false;
        if (c.LohSizes.Count >= 3)
        {
            int third = Math.Max(1, c.LohSizes.Count / 3);
            double firstAvg = c.LohSizes.Take(third).Average(x => (double)x.Bytes);
            double lastAvg  = c.LohSizes.Skip(c.LohSizes.Count - third).Average(x => (double)x.Bytes);
            isTrendingUp = lastAvg > firstAvg * 1.1;
        }

        IReadOnlyList<double>? timeline = null;
        if (c.LohSizes.Count > 1)
        {
            var perSecond = new Dictionary<int, double>();
            foreach (var (ms, bytes) in c.LohSizes)
            {
                int bucket = (int)(ms / 1000.0);
                perSecond[bucket] = bytes;
            }
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            double lastVal = 0;
            for (int b = 0; b <= maxB - minB; b++)
            {
                lastVal = perSecond.TryGetValue(b + minB, out double v) ? v : lastVal;
                tl[b] = lastVal;
            }
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  LOH: {FormatBytes(startLoh)} → peak {FormatBytes(peakLoh)}" +
                      (isTrendingUp ? "  ⚠ growing" : "");

        return new LohTraceData(info, processFilter,
            startLoh, peakLoh, endLoh, growth, c.LohSizes.Count,
            c.Gen2WithGrowth, isTrendingUp, timeline, HasData: true);
    }

    public LohTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new LohTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, false, null, false);
        }
    }


}
