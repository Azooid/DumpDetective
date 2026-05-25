using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies outbound HTTP events from:
///   System.Net.Http               (connection lifecycle, request headers/content)
///   System.Diagnostics.Eventing.FrameworkEventSource  (GetResponse, GetRequestStream)
///   Microsoft-Windows-ASPNET      (Request/Start|Stop)
///   Microsoft-AspNetCore-Hosting  (Request/Start|Stop)
/// Also covers ASP.NET Core pipeline events (routing, auth) that appear on AspNetCore providers.
///
/// NOTE: KestrelClassifier is registered first; inbound Kestrel Request/* events are
/// claimed there before this classifier is consulted.
/// </summary>
internal sealed class HttpClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } =
    [
        "System.Net.Http",
        "Microsoft-Windows-ASPNET",
        "Microsoft-AspNetCore-Hosting",
        "System.Diagnostics.Eventing.FrameworkEventSource",
    ];

    public TraceEventKind Classify(string provider, string name)
    {
        // ── ASP.NET Core pipeline — routing and auth ──────────────────────────
        if (Contains(name, "AspNetReq") || Contains(name, "AspNetTrace"))
        {
            if (EndsWith(name, "Start")) return HttpRequestStart;
            if (EndsWith(name, "Stop"))  return HttpRequestStop;
        }
        if (Contains(name, "AspNetCore") || Contains(name, "Microsoft.AspNetCore"))
        {
            if (Contains(name, "RouteMatch") || Contains(name, "Routing"))  return AspNetCoreRouteMatched;
            if (Contains(name, "Authentication") || Contains(name, "Auth"))
            {
                if (EndsWith(name, "Start"))                                return AspNetCoreAuthStart;
                if (EndsWith(name, "Stop"))                                 return AspNetCoreAuthStop;
                if (Contains(name, "Fail") || Contains(name, "Challenge")) return AspNetCoreAuthFailed;
            }
        }

        // ── FrameworkEventSource — HttpClient-level events ────────────────────
        if (Contains(name, "GetRequestStream"))
        {
            if (Contains(name, "Start")) return HttpClientGetRequestStart;
            if (Contains(name, "Stop"))  return HttpClientGetRequestStop;
        }
        if (Contains(name, "GetResponse") && !Contains(name, "GetResponseHeader"))
        {
            if (Contains(name, "Start")) return HttpClientGetResponseStart;
            if (Contains(name, "Stop"))  return HttpClientGetResponseStop;
        }

        // ── System.Net.Http connection lifecycle ──────────────────────────────
        if (Contains(name, "ConnectionEstablished")) return HttpConnectionEstablished;
        if (Contains(name, "ConnectionClosed"))      return HttpConnectionClosed;
        if (Contains(name, "RequestLeftQueue"))       return HttpRequestLeftQueue;

        // ── Request headers / content ─────────────────────────────────────────
        if (Contains(name, "ResponseHeaders/Start") || Contains(name, "ResponseHeadersStart")) return HttpResponseHeadersStart;
        if (Contains(name, "ResponseHeaders/Stop")  || Contains(name, "ResponseHeadersStop"))  return HttpResponseHeadersStop;
        if (Contains(name, "RequestHeaders/Start")  || (Contains(name, "RequestHeaders") && EndsWith(name, "Start"))) return HttpRequestHeadersStart;
        if (Contains(name, "RequestHeaders/Stop")   || (Contains(name, "RequestHeaders") && EndsWith(name, "Stop")))  return HttpRequestHeadersStop;
        if (Contains(name, "RequestContent/Start")  || (Contains(name, "RequestContent") && EndsWith(name, "Start"))) return HttpRequestContentStart;
        if (Contains(name, "RequestContent/Stop")   || (Contains(name, "RequestContent") && EndsWith(name, "Stop")))  return HttpRequestContentStop;

        // ── Request start / stop — skip if this is a Kestrel provider ─────────
        if (Contains(name, "Request/Start") || Contains(name, "RequestStart") ||
            Contains(name, "BeginRequest"))
        {
            if (!Contains(provider, "Kestrel") && !Contains(name, "Kestrel"))
                return HttpRequestStart;
        }
        if (Contains(name, "Request/Stop") || Contains(name, "RequestStop") ||
            Contains(name, "EndRequest"))
        {
            if (!Contains(provider, "Kestrel") && !Contains(name, "Kestrel"))
                return HttpRequestStop;
        }

        return Unknown;
    }
}
