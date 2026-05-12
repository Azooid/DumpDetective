using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses Microsoft.AspNetCore.* EventSource events (routing, auth, diagnostics)
/// to detect endpoint bottlenecks, auth failure storms, and unmatched routes.
/// </summary>
public sealed class AspNetCorePipelineAnalyzer
{
    public AspNetCorePipelineData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new AspNetCorePipelineData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }
    }

    public AspNetCorePipelineData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                           string? processFilter = null, Action<string>? progress = null)
    {
        var byRoute     = new Dictionary<string, RouteAcc>(StringComparer.OrdinalIgnoreCase);
        var authTimeline = new Dictionary<int, int>();

        int totalReq = 0, totalErr = 0, authFail = 0, unmatched = 0;
        var pendingAuth = new Dictionary<int, (double StartMs, string Route)>();
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
                    progress($"{totalReq:N0} requests  \u2022  {totalErr:N0} errors");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isAspNetCore = evName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                    ev.ProviderName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                    ev.ProviderName.Contains("Microsoft.AspNet", StringComparison.OrdinalIgnoreCase);
                if (!isAspNetCore) continue;

                // Route matched
                if (evName.Contains("RouteMatch",  StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("Routing",      StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("MatchSuccess", StringComparison.OrdinalIgnoreCase))
                {
                    totalReq++;
                    string route = SafeStr(ev, "RoutePattern");
                    if (route.Length == 0) route = SafeStr(ev, "Route");
                    if (route.Length == 0) route = SafeStr(ev, "Path");
                    if (route.Length == 0) route = "(unknown)";

                    if (!byRoute.TryGetValue(route, out var acc))
                        byRoute[route] = acc = new RouteAcc();
                    acc.Count++;

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                }
                else if (evName.Contains("NoMatch",  StringComparison.OrdinalIgnoreCase) ||
                         evName.Contains("MatchFail", StringComparison.OrdinalIgnoreCase))
                {
                    unmatched++;
                }
                else if (evName.Contains("Auth", StringComparison.OrdinalIgnoreCase))
                {
                    if (evName.Contains("Fail",    StringComparison.OrdinalIgnoreCase) ||
                        evName.Contains("Forbid",  StringComparison.OrdinalIgnoreCase) ||
                        evName.Contains("Challenge",StringComparison.OrdinalIgnoreCase))
                    {
                        authFail++;
                        int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                        authTimeline.TryGetValue(bucket, out int pv);
                        authTimeline[bucket] = pv + 1;
                        // Mark the current active route as having auth failure
                        string route = SafeStr(ev, "RoutePattern");
                        if (route.Length == 0) route = SafeStr(ev, "Path");
                        if (route.Length > 0 && byRoute.TryGetValue(route, out var acc))
                            acc.AuthFailures++;
                    }
                }
                else if (evName.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                         evName.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                {
                    totalErr++;
                    string route = SafeStr(ev, "RoutePattern");
                    if (route.Length == 0) route = SafeStr(ev, "Path");
                    if (route.Length > 0 && byRoute.TryGetValue(route, out var acc))
                        acc.Errors++;
                }
            }
        }
        catch (Exception ex)
        {
            return new AspNetCorePipelineData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }

        if (totalReq == 0 && authFail == 0)
        {
            return new AspNetCorePipelineData(
                $"{traceFileName}  |  0 ASP.NET Core pipeline events — collect with " +
                "--providers 'Microsoft.AspNetCore:0xFF:5,Microsoft.AspNetCore.Routing:0xFF:5,Microsoft.AspNetCore.Authorization:0xFF:5'",
                processFilter, 0, 0, 0, 0, [], null, false);
        }

        var topEndpoints = byRoute
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new AspNetCoreEndpointSummary(kv.Key,
                kv.Value.Count, kv.Value.Errors, kv.Value.AuthFailures,
                kv.Value.TotalMs, kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0))
            .ToList();

        IReadOnlyList<double>? timeline = null;
        if (authTimeline.Count > 1)
        {
            int minB = authTimeline.Keys.Min(), maxB = authTimeline.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in authTimeline) tl[kv.Key - minB] = kv.Value;
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {totalReq:N0} routes matched  •  {authFail} auth failures  •  {unmatched} unmatched";

        return new AspNetCorePipelineData(info, processFilter,
            totalReq, totalErr, authFail, unmatched, topEndpoints, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private sealed class RouteAcc
    {
        public int Count, Errors, AuthFailures;
        public double TotalMs;
    }
}
