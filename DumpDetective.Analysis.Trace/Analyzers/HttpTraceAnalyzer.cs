using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;


namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses HTTP request events from ASP.NET Core (Microsoft-AspNetCore-Hosting) and
/// classic ASP.NET / IIS (System.Web, Microsoft-Windows-ASPNET) providers in a trace.
///
/// Surfaces: request count, latency percentiles, slow requests, error rate, top paths.
/// </summary>
public sealed class HttpTraceAnalyzer
{
    // Requests considered "slow" if they exceed this latency threshold (ms).
    public const double DefaultSlowThresholdMs = 1000.0;

    public HttpTraceData Analyze(string tracePath, int top = 20, string? processFilter = null,
                                  double slowThresholdMs = DefaultSlowThresholdMs)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter, slowThresholdMs);
        }
        catch (Exception ex)
        {
            return Empty($"Failed: {ex.Message}", processFilter, slowThresholdMs);
        }
    }

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter, double slowThresholdMs) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly double SlowThresholdMs = slowThresholdMs;
        internal readonly Dictionary<string, RequestStart> InFlightIis = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, RequestStart> InFlightOther = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<HttpRequestEntry> CompletedIis = new();
        internal readonly List<HttpRequestEntry> CompletedOther = new();

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(evName, out byte kind))
                EvKind[evName] = kind = ComputeHttpKind(evName);
            if (kind == 0) return;

            bool useIis     = kind <= 2;
            bool isAspTrace = kind == 3 || kind == 4;
            bool isStart    = (kind & 1) == 1;
            var activeInFlight  = useIis ? InFlightIis  : InFlightOther;
            var activeCompleted = useIis ? CompletedIis : CompletedOther;

            string correlationKey;
            if (useIis)
            {
                correlationKey = SafeStr(ev, "RequestId");
            }
            else if (isAspTrace)
            {
                correlationKey = SafeStr(ev, "ContextId");
                if (correlationKey.Length == 0) correlationKey = SafeStr(ev, "contextId");
            }
            else
            {
                correlationKey = SafeStr(ev, "requestId");
                if (correlationKey.Length == 0) correlationKey = SafeStr(ev, "RequestId");
            }
            if (correlationKey.Length == 0)
                correlationKey = ev.ActivityID != Guid.Empty
                    ? ev.ActivityID.ToString()
                    : $"thread-{threadId}";

            if (isStart)
            {
                string method = useIis ? SafeStr(ev, "RequestMethod") : SafeStr(ev, "requestMethod");
                if (method.Length == 0) method = SafeStr(ev, "Method");
                if (method.Length == 0) method = SafeStr(ev, "HttpMethod");
                if (method.Length == 0) method = SafeStr(ev, "Verb");
                if (method.Length == 0) method = "GET";
                string path = useIis ? SafeStr(ev, "RequestPath") : SafeStr(ev, "requestPath");
                if (path.Length == 0) path = SafeStr(ev, "Path");
                if (path.Length == 0) path = SafeStr(ev, "RequestPath");
                if (path.Length == 0) path = SafeStr(ev, "Url");
                if (path.Length == 0) path = SafeStr(ev, "RequestUrl");
                if (path.Length == 0) path = "/";
                activeInFlight[correlationKey] = new RequestStart(method, path,
                    timestampMs, threadId);
                return;
            }

            if (!activeInFlight.TryGetValue(correlationKey, out var req))
            {
                string path2 = useIis ? SafeStr(ev, "RequestPath") : SafeStr(ev, "requestPath");
                if (path2.Length == 0) path2 = SafeStr(ev, "Path");
                if (path2.Length == 0) path2 = SafeStr(ev, "RequestPath");
                if (path2.Length == 0) path2 = "/";
                int sc2 = SafeInt(ev, "StatusCode");
                if (sc2 == 0) sc2 = SafeInt(ev, "statusCode");
                if (sc2 == 0) sc2 = 200;
                long ticks2 = SafeLong(ev, "elapsed");
                double dur2 = ticks2 > 0 ? ticks2 / (double)TimeSpan.TicksPerMillisecond : 0;
                activeCompleted.Add(new HttpRequestEntry("?", path2, sc2, dur2,
                    timestampMs, threadId));
                return;
            }

            activeInFlight.Remove(correlationKey);
            long elapsedTicks = SafeLong(ev, "elapsed");
            double durationMs = elapsedTicks > 0
                ? elapsedTicks / (double)TimeSpan.TicksPerMillisecond
                : Math.Max(0, timestampMs - req.StartMs);
            int statusCode = SafeInt(ev, "StatusCode");
            if (statusCode == 0) statusCode = SafeInt(ev, "statusCode");
            if (statusCode == 0) statusCode = 200;

            activeCompleted.Add(new HttpRequestEntry(req.Method, req.Path, statusCode,
                durationMs, req.StartMs, threadId));
        }

        public bool WantsEvent(string eventName) { if (!EvKind.TryGetValue(eventName, out byte v)) EvKind[eventName] = v = ComputeHttpKind(eventName); return v != 0; }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null,
                                    double slowThresholdMs = DefaultSlowThresholdMs)
        => new Consumer(processFilter, slowThresholdMs);

    public HttpTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                      string? processFilter = null)
    {
        var c = (Consumer)consumer;
        var completed = c.CompletedIis.Count > 0 ? c.CompletedIis : c.CompletedOther;

        if (completed.Count == 0)
        {
            return new HttpTraceData(
                $"{traceFileName}  |  0 HTTP request events — collect with " +
                "--providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5' or " +
                "'Microsoft-Windows-ASPNET:0xFFFF:5' (IIS/classic ASP.NET)",
                processFilter, 0, 0, 0, 0, 0, 0, 0, 0, c.SlowThresholdMs, [], [], [], false);
        }

        var sorted      = completed.Where(r => r.DurationMs > 0)
                                    .OrderBy(r => r.DurationMs).ToList();
        double totalMs  = completed.Sum(r => r.DurationMs);
        double avgMs    = completed.Count > 0 ? totalMs / completed.Count : 0;
        double maxMs    = completed.Count > 0 ? completed.Max(r => r.DurationMs) : 0;
        double p95Ms    = Percentile(sorted, 0.95);
        double p99Ms    = Percentile(sorted, 0.99);
        int    errors   = completed.Count(r => r.StatusCode >= 400);
        var    slow     = completed.Where(r => r.DurationMs >= c.SlowThresholdMs)
                                    .OrderByDescending(r => r.DurationMs).Take(top).ToList();

        var byStatus = completed
            .GroupBy(r => r.StatusCode)
            .Select(g => new HttpStatusSummary(
                g.Key, g.Count(),
                g.Average(r => r.DurationMs)))
            .OrderBy(s => s.StatusCode)
            .ToList();

        var topPaths = completed
            .GroupBy(r => NormalisePath(r.Path))
            .Select(g => new HttpPathSummary(
                g.Key,
                g.Count(),
                g.Average(r => r.DurationMs),
                g.Max(r => r.DurationMs),
                g.Count(r => r.StatusCode >= 400)))
            .OrderByDescending(p => p.Count)
            .Take(top)
            .ToList();

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {completed.Count:N0} requests  •  {errors} errors";

        return new HttpTraceData(info, processFilter,
            completed.Count, totalMs, avgMs, maxMs, p95Ms, p99Ms,
            errors, slow.Count, c.SlowThresholdMs,
            slow, byStatus, topPaths, HasData: true);
    }

    public HttpTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                  string? processFilter = null,
                                  double slowThresholdMs = DefaultSlowThresholdMs,
                                  Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter, slowThresholdMs);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return Empty($"Parse error: {ex.Message}", processFilter, slowThresholdMs);
        }
    }

    /// <summary>
    /// Classify event name once: 0=skip, 1=start_IIS, 2=stop_IIS,
    /// 3=start_AspNetTrace, 4=stop_AspNetTrace, 5=start_other, 6=stop_other.
    /// Odd=start, Even=stop (for kind>0). 1|2=IIS, 3|4=AspNetTrace, 5|6=other.
    /// </summary>
    private static byte ComputeHttpKind(string n)
    {
        // IIS / Classic ASP.NET (Microsoft-Windows-ASPNET, System.Web)
        if (n.IndexOf("ASPNET",     StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("System.Web", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (n.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (n.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (n.IndexOf("Stop",  StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            }
        }
        // AspNetTrace (AspNetReq)
        if (n.IndexOf("AspNetReq", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (n.EndsWith("/Start", StringComparison.OrdinalIgnoreCase)) return 3;
            if (n.EndsWith("/Stop",  StringComparison.OrdinalIgnoreCase)) return 4;
        }
        // ASP.NET Core
        if (n.IndexOf("AspNetCore", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (n.EndsWith("RequestStart",    StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("Request/Start",   StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("RequestIn/Start", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("Incoming/Start",  StringComparison.OrdinalIgnoreCase)) return 5;
            if (n.EndsWith("RequestStop",    StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("Request/Stop",   StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("RequestIn/Stop", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("Incoming/Stop",  StringComparison.OrdinalIgnoreCase)) return 6;
        }
        // FrameworkEventSource (HttpWebRequest / HttpClient pre-.NET Core)
        if (n.IndexOf("FrameworkEventSource", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (n.IndexOf("GetResponse",      StringComparison.OrdinalIgnoreCase) >= 0 ||
             n.IndexOf("GetRequestStream", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            if (n.EndsWith("/Start", StringComparison.OrdinalIgnoreCase)) return 5;
            if (n.EndsWith("/Stop",  StringComparison.OrdinalIgnoreCase)) return 6;
        }
        return 0;
    }

    private static double Percentile(List<HttpRequestEntry> sorted, double pct)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(pct * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)].DurationMs;
    }

    // Normalise paths: strip numeric IDs (/api/user/123 → /api/user/{id})
    private static string NormalisePath(string path)
    {
        if (path.Length == 0) return "/";
        // Replace numeric segments with {id}
        var segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && segments[i].All(char.IsAsciiDigit))
                segments[i] = "{id}";
        }
        // Also replace GUIDs
        return string.Join('/',
            segments.Select(s => s.Length == 36 && s.Count(c => c == '-') == 4 ? "{guid}" : s));
    }


    private static HttpTraceData Empty(string info, string? process, double slowMs) =>
        new(info, process, 0, 0, 0, 0, 0, 0, 0, 0, slowMs, [], [], [], false);

    private sealed record RequestStart(string Method, string Path, double StartMs, int ThreadId);
}
