using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

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

    public LohTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                 string? processFilter = null, Action<string>? progress = null)
    {
        var lohSizes = new List<(double TimeMs, long Bytes)>();
        int gen2WithGrowth = 0;
        long prevLoh = -1;
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
                    progress($"{lohSizes.Count:N0} heap stats");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isHeapStats =
                    evName.EndsWith("GCHeapStats",    StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("GC/HeapStats",   StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("HeapStats",      StringComparison.OrdinalIgnoreCase);
                if (!isHeapStats) continue;

                long loh = SafeLong(ev, "GenerationSize3");
                if (loh <= 0)
                {
                    // Some providers report as "LohSize" or "Gen3Size"
                    loh = SafeLong(ev, "LohSize");
                    if (loh <= 0) loh = SafeLong(ev, "Gen3Size");
                }
                if (loh <= 0) continue;

                lohSizes.Add((ev.TimeStampRelativeMSec, loh));

                if (prevLoh > 0 && loh > prevLoh)
                    gen2WithGrowth++;
                prevLoh = loh;
            }
        }
        catch (Exception ex)
        {
            return new LohTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, false, null, false);
        }

        if (lohSizes.Count == 0)
        {
            return new LohTraceData(
                $"{traceFileName}  |  0 GCHeapStats events — collect with " +
                "--profile gc-verbose or --providers 'Microsoft-Windows-DotNETRuntime:0x1:4'",
                processFilter, 0, 0, 0, 0, 0, 0, false, null, false);
        }

        long startLoh = lohSizes[0].Bytes;
        long peakLoh  = lohSizes.Max(x => x.Bytes);
        long endLoh   = lohSizes[^1].Bytes;
        long growth   = peakLoh - startLoh;

        // Trend detection: compare first-third average vs last-third average
        bool isTrendingUp = false;
        if (lohSizes.Count >= 3)
        {
            int third = Math.Max(1, lohSizes.Count / 3);
            double firstAvg = lohSizes.Take(third).Average(x => (double)x.Bytes);
            double lastAvg  = lohSizes.Skip(lohSizes.Count - third).Average(x => (double)x.Bytes);
            isTrendingUp = lastAvg > firstAvg * 1.1; // >10% growth = trending
        }

        // Build sparkline (one data point per HeapStats = one per GC)
        IReadOnlyList<double>? timeline = null;
        if (lohSizes.Count > 1)
        {
            // Map to per-second buckets
            var perSecond = new Dictionary<int, double>();
            foreach (var (ms, bytes) in lohSizes)
            {
                int bucket = (int)(ms / 1000.0);
                perSecond[bucket] = bytes; // last LOH size in that second
            }
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            // Forward-fill gaps
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
            startLoh, peakLoh, endLoh, growth, lohSizes.Count,
            gen2WithGrowth, isTrendingUp, timeline, HasData: true);
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0; }
    }

    private static string FormatBytes(long b)
    {
        if (b >= 1L << 30) return $"{b / (double)(1 << 30):F2} GB";
        if (b >= 1L << 20) return $"{b / (double)(1 << 20):F1} MB";
        return $"{b / (double)(1 << 10):F1} KB";
    }
}
