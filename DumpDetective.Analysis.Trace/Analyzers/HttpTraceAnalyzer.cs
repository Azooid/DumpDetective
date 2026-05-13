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

    public HttpTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                  string? processFilter = null,
                                  double slowThresholdMs = DefaultSlowThresholdMs,
                                  Action<string>? progress = null)
    {
        // Separate tracking per ETW provider to prevent double-counting.
        //
        // Both Microsoft-Windows-ASPNET and AspNetTrace/AspNetReq fire for EVERY IIS request.
        // They share the same ETW ActivityID, so merging them into one dict causes start-
        // overwrite collisions and orphaned stops, breaking both counts and durations.
        //
        // Strategy:
        //   inFlightIis   / completedIis   ← Microsoft-Windows-ASPNET (canonical IIS provider)
        //   inFlightOther / completedOther ← AspNetTrace/AspNetReq, ASP.NET Core, FrameworkEventSource
        // At the end prefer IIS results; fall back to Other when IIS events are absent.
        var inFlightIis   = new Dictionary<string, RequestStart>(StringComparer.OrdinalIgnoreCase);
        var inFlightOther = new Dictionary<string, RequestStart>(StringComparer.OrdinalIgnoreCase);
        var completedIis   = new List<HttpRequestEntry>();
        var completedOther = new List<HttpRequestEntry>();
        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;
        var evKind = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var ev in trace.Events)
            {
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{completedIis.Count + completedOther.Count:N0} requests");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !(ev.ProcessName ?? "").Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                if (!evKind.TryGetValue(evName, out byte kind))
                    evKind[evName] = kind = ComputeHttpKind(evName);
                if (kind == 0) continue;

                // Routing: odd=start, even=stop; 1|2=IIS, 3|4=AspNetTrace, 5|6=other
                bool useIis     = kind <= 2;
                bool isAspTrace = kind == 3 || kind == 4;
                bool isStart    = (kind & 1) == 1;
                var activeInFlight  = useIis ? inFlightIis  : inFlightOther;
                var activeCompleted = useIis ? completedIis : completedOther;

                // ── Correlation key ───────────────────────────────────────────
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
                        : $"thread-{ev.ThreadID}";

                if (isStart)
                {
                    // Method: IIS uses PascalCase; ASP.NET Core EventSource uses camelCase
                    string method = useIis ? SafeStr(ev, "RequestMethod") : SafeStr(ev, "requestMethod");
                    if (method.Length == 0) method = SafeStr(ev, "Method");
                    if (method.Length == 0) method = SafeStr(ev, "HttpMethod");
                    if (method.Length == 0) method = SafeStr(ev, "Verb");
                    if (method.Length == 0) method = "GET";
                    // Path: IIS uses PascalCase; ASP.NET Core EventSource uses camelCase
                    string path = useIis ? SafeStr(ev, "RequestPath") : SafeStr(ev, "requestPath");
                    if (path.Length == 0) path = SafeStr(ev, "Path");
                    if (path.Length == 0) path = SafeStr(ev, "RequestPath");
                    if (path.Length == 0) path = SafeStr(ev, "Url");
                    if (path.Length == 0) path = SafeStr(ev, "RequestUrl");
                    if (path.Length == 0) path = "/";
                    activeInFlight[correlationKey] = new RequestStart(method, path,
                        ev.TimeStampRelativeMSec, ev.ThreadID);
                    continue;
                }

                // isStop
                if (!activeInFlight.TryGetValue(correlationKey, out var req))
                {
                    // Orphaned stop — request started before trace began, or correlation mismatch.
                    // Still record so the count is accurate.
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
                        ev.TimeStampRelativeMSec, ev.ThreadID));
                    continue;
                }

                activeInFlight.Remove(correlationKey);
                // ASP.NET Core stop events carry an "elapsed" field (TimeSpan ticks = 100-ns units);
                // prefer it over computing from timestamps for accuracy across async hops.
                long elapsedTicks = SafeLong(ev, "elapsed");
                double durationMs = elapsedTicks > 0
                    ? elapsedTicks / (double)TimeSpan.TicksPerMillisecond
                    : Math.Max(0, ev.TimeStampRelativeMSec - req.StartMs);
                int statusCode = SafeInt(ev, "StatusCode");
                if (statusCode == 0) statusCode = SafeInt(ev, "statusCode");
                if (statusCode == 0) statusCode = 200;

                activeCompleted.Add(new HttpRequestEntry(req.Method, req.Path, statusCode,
                    durationMs, req.StartMs, ev.ThreadID));
            }
        }
        catch (Exception ex)
        {
            return Empty($"Parse error: {ex.Message}", processFilter, slowThresholdMs);
        }

        // Prefer IIS-sourced events (Microsoft-Windows-ASPNET) when present; they are the
        // canonical IIS provider and give reliable RequestId-based correlation. Fall back
        // to the other providers only when no IIS events were seen in this trace.
        var completed = completedIis.Count > 0 ? completedIis : completedOther;

        if (completed.Count == 0)
        {
            return new HttpTraceData(
                $"{traceFileName}  |  0 HTTP request events — collect with " +
                "--providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5' or " +
                "'Microsoft-Windows-ASPNET:0xFFFF:5' (IIS/classic ASP.NET)",
                processFilter, 0, 0, 0, 0, 0, 0, 0, 0, slowThresholdMs, [], [], [], false);
        }

        // ── Compute statistics ────────────────────────────────────────────────
        var sorted      = completed.Where(r => r.DurationMs > 0)
                                    .OrderBy(r => r.DurationMs).ToList();
        double totalMs  = completed.Sum(r => r.DurationMs);
        double avgMs    = completed.Count > 0 ? totalMs / completed.Count : 0;
        double maxMs    = completed.Count > 0 ? completed.Max(r => r.DurationMs) : 0;
        double p95Ms    = Percentile(sorted, 0.95);
        double p99Ms    = Percentile(sorted, 0.99);
        int    errors   = completed.Count(r => r.StatusCode >= 400);
        var    slow     = completed.Where(r => r.DurationMs >= slowThresholdMs)
                                    .OrderByDescending(r => r.DurationMs).Take(top).ToList();

        // ── Status code summary ───────────────────────────────────────────────
        var byStatus = completed
            .GroupBy(r => r.StatusCode)
            .Select(g => new HttpStatusSummary(
                g.Key, g.Count(),
                g.Average(r => r.DurationMs)))
            .OrderBy(s => s.StatusCode)
            .ToList();

        // ── Top paths by count ────────────────────────────────────────────────
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
            errors, slow.Count, slowThresholdMs,
            slow, byStatus, topPaths, HasData: true);
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

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private static int SafeInt(TraceEvent ev, string field)
    {
        try
        {
            var raw = ev.PayloadByName(field);
            return raw is not null ? Convert.ToInt32(raw) : 0;
        }
        catch { return 0; }
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try
        {
            var raw = ev.PayloadByName(field);
            return raw is not null ? Convert.ToInt64(raw) : 0L;
        }
        catch { return 0L; }
    }

    private static HttpTraceData Empty(string info, string? process, double slowMs) =>
        new(info, process, 0, 0, 0, 0, 0, 0, 0, 0, slowMs, [], [], [], false);

    private sealed record RequestStart(string Method, string Path, double StartMs, int ThreadId);
}
