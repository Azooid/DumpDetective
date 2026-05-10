using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Detects allocation burst patterns from GCAllocationTick events.
/// Buckets ticks into 500 ms windows and identifies periods where the rate
/// exceeds 3× the rolling median — indicating spikes vs. sustained load.
/// </summary>
public sealed class AllocationBurstAnalyzer
{
    public AllocationBurstData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new AllocationBurstData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, [], null, false);
        }
    }

    public AllocationBurstData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        // 500 ms buckets: key = (int)(ms / 500)
        var buckets = new Dictionary<int, BucketAcc>();

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isTick =
                    evName.Contains("AllocationTick", StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("GC/AllocationTick", StringComparison.OrdinalIgnoreCase);
                if (!isTick) continue;

                long bytes = 0;
                try { bytes = (long)Convert.ChangeType(ev.PayloadByName("AllocationAmount"), typeof(long)); } catch { }
                if (bytes <= 0) bytes = 100 * 1024; // default ~100 KB per tick

                string typeName = SafeStr(ev, "TypeName");
                if (typeName.Length == 0) typeName = "(unknown)";

                int bucket = (int)(ev.TimeStampRelativeMSec / 500.0);
                if (!buckets.TryGetValue(bucket, out var acc))
                    buckets[bucket] = acc = new BucketAcc();
                acc.Bytes += bytes;
                acc.Count++;
                if (!acc.TypeCounts.TryGetValue(typeName, out long prev))
                    acc.TypeCounts[typeName] = prev;
                acc.TypeCounts[typeName] = prev + bytes;
            }
        }
        catch (Exception ex)
        {
            return new AllocationBurstData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, [], null, false);
        }

        if (buckets.Count == 0)
        {
            return new AllocationBurstData(
                $"{traceFileName}  |  0 allocation ticks — collect with --providers " +
                "'Microsoft-Windows-DotNETRuntime:0x8000:4' (GCKeyword with AllocationTick)",
                processFilter, 0, 0, 0, [], null, false);
        }

        // Build sorted rate list (KB/s per 500ms window = bytes / 500ms * 1000ms = bytes * 2 / 1024)
        var rates = buckets.OrderBy(kv => kv.Key)
                           .Select(kv => (BucketMs: kv.Key * 500.0,
                                          RateKbPerSec: kv.Value.Bytes * 2.0 / 1024.0,
                                          TopType: kv.Value.TypeCounts.OrderByDescending(x => x.Value).First().Key,
                                          Bytes: kv.Value.Bytes))
                           .ToList();

        // Compute rolling median for burst detection (simple: use overall median)
        var sortedRates = rates.Select(r => r.RateKbPerSec).OrderBy(v => v).ToList();
        double median = sortedRates.Count > 0
            ? sortedRates[sortedRates.Count / 2]
            : 0;
        double burstThreshold = Math.Max(median * 3.0, 1024.0); // at least 1 MB/s

        double avgRate = rates.Average(r => r.RateKbPerSec);
        double peakRate = rates.Max(r => r.RateKbPerSec);

        // Detect burst periods: consecutive windows above threshold
        var bursts = new List<AllocationBurstEntry>();
        int i = 0;
        while (i < rates.Count)
        {
            if (rates[i].RateKbPerSec < burstThreshold) { i++; continue; }

            int start = i;
            double peakInBurst = rates[i].RateKbPerSec;
            long totalBytes = rates[i].Bytes;
            string topType = rates[i].TopType;

            while (i < rates.Count && rates[i].RateKbPerSec >= burstThreshold)
            {
                if (rates[i].RateKbPerSec > peakInBurst)
                {
                    peakInBurst = rates[i].RateKbPerSec;
                    topType = rates[i].TopType;
                }
                totalBytes += rates[i].Bytes;
                i++;
            }

            bursts.Add(new AllocationBurstEntry(
                rates[start].BucketMs,
                rates[i > 0 ? i - 1 : 0].BucketMs + 500,
                peakInBurst, totalBytes, topType));
        }

        var topBursts = bursts.OrderByDescending(b => b.PeakRateKbPerSec).Take(top).ToList();

        // Build sparkline from per-second (combine 500ms buckets → 1s)
        IReadOnlyList<double>? timeline = null;
        if (rates.Count > 1)
        {
            int minB = buckets.Keys.Min() / 2, maxB = buckets.Keys.Max() / 2;
            var tl = new double[maxB - minB + 1];
            foreach (var kv in buckets)
            {
                int secBucket = kv.Key / 2 - minB;
                tl[secBucket] += kv.Value.Bytes * 2.0 / 1024.0;
            }
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {bursts.Count} burst period(s)  •  peak {peakRate:F0} KB/s";

        return new AllocationBurstData(info, processFilter,
            bursts.Count, peakRate, avgRate, topBursts, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class BucketAcc
    {
        public long Bytes;
        public int Count;
        public Dictionary<string, long> TypeCounts = new(StringComparer.OrdinalIgnoreCase);
    }
}
