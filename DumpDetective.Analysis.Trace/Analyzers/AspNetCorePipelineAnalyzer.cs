using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


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

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            // For DiagnosticSource wrappers (SourceName="Microsoft.AspNetCore"), resolve inner EventName.
            string eventName = meta.EventName;
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
            {
                string srcName = SafeStr(ev, "SourceName");
                if (!srcName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) &&
                    !srcName.Contains("Microsoft.AspNet", StringComparison.OrdinalIgnoreCase))
                    return;
                string inner = SafeStr(ev, "EventName");
                if (inner.Length > 0) eventName = inner;
            }
            else
            {
                bool isAspNetCore = meta.EventName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                    ev.ProviderName.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                                    ev.ProviderName.Contains("Microsoft.AspNet", StringComparison.OrdinalIgnoreCase);
                if (!isAspNetCore) return;
            }

            // Route matched
            if (eventName.Contains("RouteMatch",     StringComparison.OrdinalIgnoreCase) ||
                eventName.Contains("Routing",         StringComparison.OrdinalIgnoreCase) ||
                eventName.Contains("MatchSuccess",    StringComparison.OrdinalIgnoreCase) ||
                eventName.Contains("EndpointMatched", StringComparison.OrdinalIgnoreCase))
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
            else if (eventName.Contains("NoMatch",  StringComparison.OrdinalIgnoreCase) ||
                     eventName.Contains("MatchFail", StringComparison.OrdinalIgnoreCase))
            {
                Unmatched++;
            }
            else if (eventName.Contains("Auth", StringComparison.OrdinalIgnoreCase))
            {
                if (eventName.Contains("Fail",    StringComparison.OrdinalIgnoreCase) ||
                    eventName.Contains("Forbid",  StringComparison.OrdinalIgnoreCase) ||
                    eventName.Contains("Challenge",StringComparison.OrdinalIgnoreCase))
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
            else if (eventName.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                     eventName.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                TotalErr++;
                string route = SafeStr(ev, "RoutePattern");
                if (route.Length == 0) route = SafeStr(ev, "Path");
                if (route.Length > 0 && ByRoute.TryGetValue(route, out var acc))
                    acc.Errors++;
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            // Microsoft-Diagnostics-DiagnosticSource wraps Microsoft.AspNetCore events;
            // the outer provider name doesn't contain "AspNetCore" — pass through to Consume for filtering.
            if (meta.ProviderName.Contains("DiagnosticSource", StringComparison.OrdinalIgnoreCase))
                return true;
            return meta.Kind switch
            {
                _ when meta.Kind == AspNetCoreRouteMatched || meta.Kind == AspNetCoreAuthStart || meta.Kind == AspNetCoreAuthStop || meta.Kind == AspNetCoreAuthFailed => true,
                _ when meta.ProviderName.Contains("AspNetCore",      StringComparison.OrdinalIgnoreCase) ||
                       meta.ProviderName.Contains("Microsoft.AspNet",StringComparison.OrdinalIgnoreCase)  => true,
                _ when meta.IsKnown => false,
                _ => meta.EventName.Contains("AspNetCore",  StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("Routing",     StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("RouteMatch",  StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("Auth",        StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("Middleware",  StringComparison.OrdinalIgnoreCase) ||
                     meta.EventName.Contains("Error",       StringComparison.OrdinalIgnoreCase)
            };
        }

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
