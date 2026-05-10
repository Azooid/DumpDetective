using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class AspNetCorePipelineReport
{
    public void Render(AspNetCorePipelineData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "ASP.NET Core pipeline analysis from routing and authorization events — identifies auth failure patterns, unmatched routes, and high-error endpoints.",
            why: "Auth failure storms can indicate misconfigured tokens, certificate expiry, or active credential stuffing attacks. Unmatched routes indicate client-side bugs or stale URLs.",
            impact: "Each auth failure generates a challenge/redirect response, adding request overhead. Unmatched routes return 404 but still traverse the full middleware pipeline.",
            bullets: [
                "Auth failures       — authentication/authorization challenges or denials",
                "Unmatched routes    — requests that did not match any endpoint",
                "Top endpoints       — routes with highest request volume or error rate"
            ],
            action: "Investigate repeated auth failures for specific identities (possible attack). " +
                    "Fix client-side calls generating unmatched routes. " +
                    "Add short-circuit middleware for unauthenticated paths to reduce overhead."
        );

        sink.Section("Trace Summary", "aspnetcore-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Routes matched",    data.TotalRequests.ToString("N0")),
            ("Errors",            data.TotalErrors.ToString("N0")),
            ("Auth failures",     data.AuthFailures > 0
                                   ? $"{data.AuthFailures} ⚠" : "0"),
            ("Unmatched routes",  data.UnmatchedRoutes > 0
                                   ? $"{data.UnmatchedRoutes} ⚠" : "0"),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No ASP.NET Core pipeline events found.",
                "Collect with: --providers 'Microsoft.AspNetCore:0xFF:5,Microsoft.AspNetCore.Routing:0xFF:5,Microsoft.AspNetCore.Authorization:0xFF:5'");
            return;
        }

        if (data.AuthFailures > 100)
            sink.Alert(AlertLevel.Warning,
                $"{data.AuthFailures} authentication/authorization failure(s) detected.",
                "High auth failure count may indicate expired tokens, misconfigured policies, or a credential stuffing attack.");

        if (data.UnmatchedRoutes > 50)
            sink.Alert(AlertLevel.Info,
                $"{data.UnmatchedRoutes} requests did not match any endpoint.",
                "Ensure client URLs are current. Unmatched routes traverse the full middleware pipeline.");

        if (data.AuthFailureTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Auth failures per second", "/s");

        if (data.TopEndpoints.Count > 0)
        {
            sink.Section("Top Endpoints", "aspnetcore-endpoints");
            var rows = new List<string[]>(Math.Min(top, data.TopEndpoints.Count));
            foreach (var e in data.TopEndpoints.Take(top))
                rows.Add([e.Route, e.Count.ToString("N0"),
                           e.ErrorCount.ToString("N0"), e.AuthFailures.ToString("N0"),
                           $"{e.AvgMs:F1} ms"]);
            sink.Table(
                ["Route", "Requests", "Errors", "Auth Failures", "Avg Duration"],
                rows, "Endpoints ordered by request count");
        }
    }
}
