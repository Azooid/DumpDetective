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

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, RouteAcc> ByRoute = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<int, int> AuthTimeline = new();
        internal int TotalReq, TotalErr, AuthFail, Unmatched;
        internal readonly Dictionary<int, (double StartMs, string Route)> PendingAuth = new();

        public void Consume(TraceEvent ev, string evName, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool isAspNetCore = evName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                ev.ProviderName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                ev.ProviderName.Contains("Microsoft.AspNet", StringComparison.OrdinalIgnoreCase);
            if (!isAspNetCore) return;

            // Route matched
            if (evName.Contains("RouteMatch",  StringComparison.OrdinalIgnoreCase) ||
                evName.Contains("Routing",      StringComparison.OrdinalIgnoreCase) ||
                evName.Contains("MatchSuccess", StringComparison.OrdinalIgnoreCase))
            {
                TotalReq++;
                string route = SafeStr(ev, "RoutePattern");
                if (route.Length == 0) route = SafeStr(ev, "Route");
                if (route.Length == 0) route = SafeStr(ev, "Path");
                if (route.Length == 0) route = "(unknown)";

                if (!ByRoute.TryGetValue(route, out var acc))
                    ByRoute[route] = acc = new RouteAcc();
                acc.Count++;
            }
            else if (evName.Contains("NoMatch",  StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("MatchFail", StringComparison.OrdinalIgnoreCase))
            {
                Unmatched++;
            }
            else if (evName.Contains("Auth", StringComparison.OrdinalIgnoreCase))
            {
                if (evName.Contains("Fail",    StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("Forbid",  StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("Challenge",StringComparison.OrdinalIgnoreCase))
                {
                    AuthFail++;
                    int bucket = (int)(timestampMs / 1000.0);
                    AuthTimeline.TryGetValue(bucket, out int pv);
                    AuthTimeline[bucket] = pv + 1;
                    // Mark the current active route as having auth failure
                    string route = SafeStr(ev, "RoutePattern");
                    if (route.Length == 0) route = SafeStr(ev, "Path");
                    if (route.Length > 0 && ByRoute.TryGetValue(route, out var acc))
                        acc.AuthFailures++;
                }
            }
            else if (evName.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                     evName.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                TotalErr++;
                string route = SafeStr(ev, "RoutePattern");
                if (route.Length == 0) route = SafeStr(ev, "Path");
                if (route.Length > 0 && ByRoute.TryGetValue(route, out var acc))
                    acc.Errors++;
            }
        }

        public bool WantsEvent(string eventName) => eventName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Routing", StringComparison.OrdinalIgnoreCase) || eventName.Contains("RouteMatch", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Auth", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Middleware", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Error", StringComparison.OrdinalIgnoreCase);

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public AspNetCorePipelineData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                               string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.TotalReq == 0 && c.AuthFail == 0)
        {
            return new AspNetCorePipelineData(
                $"{traceFileName}  |  0 ASP.NET Core pipeline events — collect with " +
                "--providers 'Microsoft.AspNetCore:0xFF:5,Microsoft.AspNetCore.Routing:0xFF:5,Microsoft.AspNetCore.Authorization:0xFF:5'",
                processFilter, 0, 0, 0, 0, [], null, false);
        }

        var topEndpoints = c.ByRoute
            .OrderByDescending(kv => kv.Value.Count)
            .Take(top)
            .Select(kv => new AspNetCoreEndpointSummary(kv.Key,
                kv.Value.Count, kv.Value.Errors, kv.Value.AuthFailures,
                0.0, 0.0))
            .ToList();

        var timeline = BuildTimeline(c.AuthTimeline);

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.TotalReq:N0} routes matched  •  {c.AuthFail} auth failures  •  {c.Unmatched} unmatched";

        return new AspNetCorePipelineData(info, processFilter,
            c.TotalReq, c.TotalErr, c.AuthFail, c.Unmatched, topEndpoints, timeline, HasData: true);
    }

    public AspNetCorePipelineData Analyze(TraceLog trace, string traceFileName, int top = 20,
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
            return new AspNetCorePipelineData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, [], null, false);
        }
    }


    private sealed class RouteAcc
    {
        public int Count, Errors, AuthFailures;
    }
}
