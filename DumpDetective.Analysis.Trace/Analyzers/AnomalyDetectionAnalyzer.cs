using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Detects statistical anomalies across metric timelines already computed
/// by Tier 1 trace analyzers. Uses a rolling z-score (mean ± 3σ over a sliding
/// window) — no new ETW event loop required.
/// </summary>
public sealed class AnomalyDetectionAnalyzer
{
    private const int BaselineWindowSec = 10;
    private const double ZScoreThreshold = 3.0;

    /// <summary>
    /// Analyzes existing timelines from completed analyzers.
    /// This overload does NOT open a trace file — it operates purely on already-computed data.
    /// </summary>
    public AnomalyDetectionData Analyze(
        string traceFileName,
        string? processFilter,
        CpuTraceData?        cpu   = null,
        GcTraceData?         gc    = null,
        AllocationBurstData? alloc = null,
        ContentionTraceData? contention = null,
        ExceptionsTraceData? exceptions = null)
    {
        var anomalies = new List<TraceAnomaly>();

        if (cpu?.SamplesTimeline is { Count: > BaselineWindowSec * 2 } cpuTl)
            DetectAnomalies(cpuTl, "CPU samples/sec", "High CPU sampling rate spike", anomalies);

        if (gc?.Events is { Count: > BaselineWindowSec * 2 } gcEvts)
        {
            // Build a per-second bucket of max GC pause
            var buckets = new Dictionary<int, double>();
            foreach (var e in gcEvts)
            {
                int b = (int)(e.TimeMs / 1000.0);
                buckets.TryGetValue(b, out double cur);
                if (e.PauseMs > cur) buckets[b] = e.PauseMs;
            }
            if (buckets.Count > BaselineWindowSec * 2)
            {
                int minB = buckets.Keys.Min(), maxB = buckets.Keys.Max();
                var pauseTl = new List<double>(maxB - minB + 1);
                for (int b = minB; b <= maxB; b++)
                    pauseTl.Add(buckets.TryGetValue(b, out double v) ? v : 0);
                DetectAnomalies(pauseTl, "GC pause ms", "Anomalous GC pause duration", anomalies);
            }
        }

        if (alloc?.RateTimeline is { Count: > BaselineWindowSec * 2 } allocTl)
            DetectAnomalies(allocTl, "Allocation KB/s", "Allocation rate anomaly", anomalies);

        if (contention?.WaitTimeline is { Count: > BaselineWindowSec * 2 } contentionTl)
            DetectAnomalies(contentionTl, "Contention wait ms/sec",
                "Contention wait time spike", anomalies);

        if (exceptions?.RateTimeline is { Count: > BaselineWindowSec * 2 } exTl)
            DetectAnomalies(exTl, "Exceptions/sec", "Exception rate anomaly", anomalies);

        if (anomalies.Count == 0)
        {
            return new AnomalyDetectionData(
                $"{traceFileName}  |  No anomalies detected across {CountMetrics(cpu, gc, alloc, contention, exceptions)} metric(s)",
                processFilter, 0, BaselineWindowSec, [], HasData: true);
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {anomalies.Count} anomaly(ies) detected";

        return new AnomalyDetectionData(info, processFilter,
            anomalies.Count, BaselineWindowSec,
            anomalies.OrderByDescending(a => a.ZScore).ToList(), HasData: true);
    }

    private static void DetectAnomalies(
        IReadOnlyList<double> timeline,
        string metricName,
        string description,
        List<TraceAnomaly> results)
    {
        int n = timeline.Count;
        for (int i = BaselineWindowSec; i < n; i++)
        {
            // Rolling baseline: [i - BaselineWindowSec, i)
            double sum = 0, sumSq = 0;
            int wStart = i - BaselineWindowSec;
            for (int j = wStart; j < i; j++)
            {
                sum   += timeline[j];
                sumSq += timeline[j] * timeline[j];
            }
            double mean = sum / BaselineWindowSec;
            double variance = sumSq / BaselineWindowSec - mean * mean;
            double stddev = variance > 0 ? Math.Sqrt(variance) : 0;

            if (stddev < 1e-6) continue; // flat metric — no meaningful z-score

            double observed = timeline[i];
            double z = (observed - mean) / stddev;

            if (z < ZScoreThreshold) continue;

            var severity = z >= 6 ? FindingSeverity.Critical :
                           z >= 4 ? FindingSeverity.Warning : FindingSeverity.Info;

            results.Add(new TraceAnomaly(
                metricName,
                i * 1000.0, // approximate timestamp (second → ms)
                observed,
                mean,
                z,
                severity,
                $"{description}: observed {observed:F1}, baseline {mean:F1} ± {stddev:F1} (z={z:F1})"));
        }
    }

    private static int CountMetrics(params object?[] items) => items.Count(x => x is not null);
}
