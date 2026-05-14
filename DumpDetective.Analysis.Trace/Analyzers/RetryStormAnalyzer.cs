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

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly List<(double TimeMs, string ExType)> RetryEvents = new();
        internal readonly Dictionary<string, int> ExTypeCounts = new(StringComparer.OrdinalIgnoreCase);

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isEx =
                evName.EndsWith("Exception/Start",  StringComparison.OrdinalIgnoreCase) ||
                evName.EndsWith("ExceptionThrown",   StringComparison.OrdinalIgnoreCase) ||
                evName.EndsWith("Exception",         StringComparison.OrdinalIgnoreCase);
            if (!isEx) return;

            string exType = SafeStr(ev, "ExceptionType");
            if (exType.Length == 0) exType = SafeStr(ev, "Type");
            if (exType.Length == 0) return;

            bool isRetry = false;
            foreach (var kw in s_retryKeywords)
            {
                if (exType.Contains(kw, StringComparison.OrdinalIgnoreCase))
                { isRetry = true; break; }
            }
            if (!isRetry) return;

            RetryEvents.Add((timestampMs, exType));
            ExTypeCounts.TryGetValue(exType, out int prev);
            ExTypeCounts[exType] = prev + 1;
        }

        public bool WantsEvent(string eventName) => eventName.EndsWith("Exception/Start", StringComparison.OrdinalIgnoreCase) || eventName.EndsWith("ExceptionThrown", StringComparison.OrdinalIgnoreCase) || eventName.EndsWith("Exception", StringComparison.OrdinalIgnoreCase);

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public RetryStormData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                       string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.RetryEvents.Count == 0)
        {
            return new RetryStormData(
                $"{traceFileName}  |  0 retry-type exception events detected",
                processFilter, 0, 0, 0, [], [], null, false);
        }

        var perSecond = new Dictionary<int, int>();
        foreach (var (ms, _) in c.RetryEvents)
        {
            int bucket = (int)(ms / 1000.0);
            perSecond.TryGetValue(bucket, out int p);
            perSecond[bucket] = p + 1;
        }

        double peakPerMin = perSecond.Values.Max() * 60.0;

        double avgPerSec = c.RetryEvents.Count / Math.Max(1.0, perSecond.Count);
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
            string topType = c.RetryEvents
                .Where(e => e.TimeMs >= startMs && e.TimeMs < endMs)
                .GroupBy(e => e.ExType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "(unknown)";

            bursts.Add(new RetryBurstEntry(startMs, endMs, burstCount,
                peakInBurst * 60.0, topType));
        }

        var affectedTypes = c.ExTypeCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => kv.Key)
            .ToList();

        var timeline = BuildTimeline(perSecond);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.RetryEvents.Count:N0} retry exceptions  •  {bursts.Count} burst(s)";

        return new RetryStormData(info, processFilter,
            c.RetryEvents.Count, bursts.Count, peakPerMin,
            bursts.OrderByDescending(b => b.ExceptionCount).Take(top).ToList(),
            affectedTypes, timeline, HasData: true);
    }

    public RetryStormData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new RetryStormData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, [], [], null, false);
        }
    }

}
