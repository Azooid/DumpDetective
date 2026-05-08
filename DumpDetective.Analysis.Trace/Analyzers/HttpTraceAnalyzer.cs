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
                                  double slowThresholdMs = DefaultSlowThresholdMs)
    {
        // Track in-flight requests by (activityId / correlationId) → start info
        var inFlight  = new Dictionary<string, RequestStart>(StringComparer.Ordinal);
        var completed = new List<HttpRequestEntry>();

        try
        {
            foreach (var ev in trace.Events)
            {
                if (processFilter is not null &&
                    !(ev.ProcessName ?? "").Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";

                // ── ASP.NET Core: Microsoft-AspNetCore-Hosting ───────────────────────────────
                bool isAspNetCoreStart =
                    evName.IndexOf("AspNetCore", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (evName.EndsWith("RequestStart",  StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("Request/Start", StringComparison.OrdinalIgnoreCase));
                bool isAspNetCoreStop =
                    evName.IndexOf("AspNetCore", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (evName.EndsWith("RequestStop",   StringComparison.OrdinalIgnoreCase) ||
                     evName.EndsWith("Request/Stop",  StringComparison.OrdinalIgnoreCase));

                // ── Classic ASP.NET (System.Web / IIS): Microsoft-Windows-ASPNET  ─────────────
                // Seen in traces as: Microsoft-Windows-ASPNET/Request/Start|Stop
                bool isAspNetStart =
                    (evName.IndexOf("ASPNET",      StringComparison.OrdinalIgnoreCase) >= 0 ||
                     evName.IndexOf("System.Web",  StringComparison.OrdinalIgnoreCase) >= 0) &&
                    evName.IndexOf("Start",   StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isAspNetStop =
                    (evName.IndexOf("ASPNET",      StringComparison.OrdinalIgnoreCase) >= 0 ||
                     evName.IndexOf("System.Web",  StringComparison.OrdinalIgnoreCase) >= 0) &&
                    evName.IndexOf("Stop",    StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0;

                // ── AspNetTrace/AspNetReq/Start|Stop  (older System.Web ETW provider) ─────────
                // Seen in traces as: AspNetTrace/AspNetReq/Start
                bool isAspNetTraceStart =
                    evName.IndexOf("AspNetReq",  StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.EndsWith("/Start",    StringComparison.OrdinalIgnoreCase);
                bool isAspNetTraceStop =
                    evName.IndexOf("AspNetReq",  StringComparison.OrdinalIgnoreCase) >= 0 &&
                    evName.EndsWith("/Stop",     StringComparison.OrdinalIgnoreCase);

                // ── FrameworkEventSource GetResponse (HttpWebRequest / HttpClient pre-.NET Core) ─
                // Seen as: System.Diagnostics.Eventing.FrameworkEventSource/GetResponse/Start|Stop
                bool isFrameworkHttpStart =
                    evName.IndexOf("FrameworkEventSource", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (evName.IndexOf("GetResponse",       StringComparison.OrdinalIgnoreCase) >= 0 ||
                     evName.IndexOf("GetRequestStream",  StringComparison.OrdinalIgnoreCase) >= 0) &&
                    evName.EndsWith("/Start", StringComparison.OrdinalIgnoreCase);
                bool isFrameworkHttpStop =
                    evName.IndexOf("FrameworkEventSource", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (evName.IndexOf("GetResponse",       StringComparison.OrdinalIgnoreCase) >= 0 ||
                     evName.IndexOf("GetRequestStream",  StringComparison.OrdinalIgnoreCase) >= 0) &&
                    evName.EndsWith("/Stop", StringComparison.OrdinalIgnoreCase);

                bool isStart = isAspNetCoreStart || isAspNetStart || isAspNetTraceStart || isFrameworkHttpStart;
                bool isStop  = isAspNetCoreStop  || isAspNetStop  || isAspNetTraceStop  || isFrameworkHttpStop;

                if (!isStart && !isStop) continue;

                // Correlation key: use activityId if present, otherwise thread-based
                string correlationKey = ev.ActivityID.ToString();
                if (correlationKey == "00000000-0000-0000-0000-000000000000")
                    correlationKey = $"thread-{ev.ThreadID}";

                if (isStart)
                {
                    // Method: ASP.NET Core uses "Method", classic ASP.NET/AspNetReq use "HttpMethod" or none
                    string method = SafeStr(ev, "Method");
                    if (method.Length == 0) method = SafeStr(ev, "HttpMethod");
                    if (method.Length == 0) method = SafeStr(ev, "Verb");
                    if (method.Length == 0) method = "GET";
                    // Path: ASP.NET Core "Path", classic "RequestPath", AspNetReq "Path" or "RequestPath" or "Url"
                    string path = SafeStr(ev, "Path");
                    if (path.Length == 0) path = SafeStr(ev, "RequestPath");
                    if (path.Length == 0) path = SafeStr(ev, "Url");
                    if (path.Length == 0) path = SafeStr(ev, "RequestUrl");
                    if (path.Length == 0) path = "/";
                    inFlight[correlationKey] = new RequestStart(method, path,
                        ev.TimeStampRelativeMSec, ev.ThreadID);
                    continue;
                }

                // isStop
                if (!inFlight.TryGetValue(correlationKey, out var req))
                {
                    // Orphaned stop — no matching start. Still record with unknown duration.
                    string path2 = SafeStr(ev, "Path");
                    if (path2.Length == 0) path2 = SafeStr(ev, "RequestPath");
                    if (path2.Length == 0) path2 = "/";
                    int   statusCode2 = SafeInt(ev, "StatusCode");
                    if (statusCode2 == 0) statusCode2 = 200;
                    completed.Add(new HttpRequestEntry("?", path2, statusCode2, 0,
                        ev.TimeStampRelativeMSec, ev.ThreadID));
                    continue;
                }

                inFlight.Remove(correlationKey);
                double durationMs = ev.TimeStampRelativeMSec - req.StartMs;
                int    statusCode = SafeInt(ev, "StatusCode");
                if (statusCode == 0) statusCode = 200;

                completed.Add(new HttpRequestEntry(req.Method, req.Path, statusCode,
                    durationMs, req.StartMs, ev.ThreadID));
            }
        }
        catch (Exception ex)
        {
            return Empty($"Parse error: {ex.Message}", processFilter, slowThresholdMs);
        }

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

    private static HttpTraceData Empty(string info, string? process, double slowMs) =>
        new(info, process, 0, 0, 0, 0, 0, 0, 0, 0, slowMs, [], [], [], false);

    private sealed record RequestStart(string Method, string Path, double StartMs, int ThreadId);
}
