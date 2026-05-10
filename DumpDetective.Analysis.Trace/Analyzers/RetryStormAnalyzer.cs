using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Detects retry storm patterns by correlating exception events whose type names
/// suggest transient failures (Timeout, Retry, Transient, Http, Socket, Rpc)
/// with HTTP error rate spikes in the same time window.
/// </summary>
public sealed class RetryStormAnalyzer
{
    private static readonly string[] s_retryKeywords =
        ["Retry", "Timeout", "Transient", "HttpRequest", "SocketException", "RpcException",
         "CircuitBreaker", "Polly", "Unavailable", "ServiceUnavailable", "GatewayTimeout"];

    public RetryStormData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new RetryStormData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, [], [], null, false);
        }
    }

    public RetryStormData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                   string? processFilter = null)
    {
        // Collect retry-type exception events with timestamps
        var retryEvents = new List<(double TimeMs, string ExType)>();
        var exTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isEx =
                    evName.EndsWith("Exception/Start",  StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("ExceptionThrown",   StringComparison.OrdinalIgnoreCase) ||
                    evName.EndsWith("Exception",         StringComparison.OrdinalIgnoreCase);
                if (!isEx) continue;

                string exType = SafeStr(ev, "ExceptionType");
                if (exType.Length == 0) exType = SafeStr(ev, "Type");
                if (exType.Length == 0) continue;

                // Check if this looks like a retry/transient exception
                bool isRetry = false;
                foreach (var kw in s_retryKeywords)
                {
                    if (exType.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    { isRetry = true; break; }
                }
                if (!isRetry) continue;

                retryEvents.Add((ev.TimeStampRelativeMSec, exType));
                exTypeCounts.TryGetValue(exType, out int prev);
                exTypeCounts[exType] = prev + 1;
            }
        }
        catch (Exception ex)
        {
            return new RetryStormData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, [], [], null, false);
        }

        if (retryEvents.Count == 0)
        {
            return new RetryStormData(
                $"{traceFileName}  |  0 retry-type exception events detected",
                processFilter, 0, 0, 0, [], [], null, false);
        }

        // Bucket into per-second counts
        var perSecond = new Dictionary<int, int>();
        foreach (var (ms, _) in retryEvents)
        {
            int bucket = (int)(ms / 1000.0);
            perSecond.TryGetValue(bucket, out int p);
            perSecond[bucket] = p + 1;
        }

        double peakPerMin = perSecond.Values.Max() * 60.0;

        // Detect burst windows: consecutive seconds with > avgRate * 2 exceptions
        double avgPerSec = retryEvents.Count / Math.Max(1.0, perSecond.Count);
        double burstThreshold = Math.Max(avgPerSec * 2.0, 3.0);
        var sortedSeconds = perSecond.OrderBy(kv => kv.Key).ToList();

        var bursts = new List<RetryBurstEntry>();
        int si = 0;
        while (si < sortedSeconds.Count)
        {
            if (sortedSeconds[si].Value < burstThreshold) { si++; continue; }
            int start = si;
            int burstCount = 0;
            double peakInBurst = 0;
            while (si < sortedSeconds.Count && sortedSeconds[si].Value >= burstThreshold)
            {
                burstCount += sortedSeconds[si].Value;
                if (sortedSeconds[si].Value > peakInBurst)
                    peakInBurst = sortedSeconds[si].Value;
                si++;
            }
            double startMs = sortedSeconds[start].Key * 1000.0;
            double endMs   = sortedSeconds[si > 0 ? si - 1 : 0].Key * 1000.0 + 1000;
            // Find top type in this window
            string topType = retryEvents
                .Where(e => e.TimeMs >= startMs && e.TimeMs < endMs)
                .GroupBy(e => e.ExType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "(unknown)";

            bursts.Add(new RetryBurstEntry(startMs, endMs, burstCount,
                peakInBurst * 60.0, topType));
        }

        var affectedTypes = exTypeCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => kv.Key)
            .ToList();

        // Sparkline
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
                      $"  |  {retryEvents.Count:N0} retry exceptions  •  {bursts.Count} burst(s)";

        return new RetryStormData(info, processFilter,
            retryEvents.Count, bursts.Count, peakPerMin,
            bursts.OrderByDescending(b => b.ExceptionCount).Take(top).ToList(),
            affectedTypes, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }
}
