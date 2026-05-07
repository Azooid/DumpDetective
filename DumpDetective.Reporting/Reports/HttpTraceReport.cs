using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HttpTraceReport
{
    public void Render(HttpTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "HTTP request trace analysis — request count, latency distribution, error rate, slow requests, and top paths.",
            why: "ASP.NET Core and IIS emit request start/stop events into ETW traces. Matching these pairs gives exact server-side request duration (excludes network) and status codes.",
            impact: "Slow requests (P95/P99) directly affect end-user experience. High error rates indicate upstream failures or unhandled exceptions. Outlier paths reveal endpoint-specific bottlenecks.",
            bullets: [
                "P95/P99 latency — how bad the worst requests are",
                "Slow requests — individual requests that exceeded the slow threshold",
                "Error rate — 4xx/5xx responses indicate application problems",
                "Top paths — which endpoints received the most traffic"
            ],
            action: "Investigate slow requests in the CPU/exception/contention chapters for the same time window. High error counts on a specific path → check ExceptionsTrace for matching exceptions."
        );

        sink.Section("HTTP Summary", "http-summary");

        if (!data.HasData || data.TotalRequests == 0)
        {
            sink.Alert(AlertLevel.Warning,
                "No HTTP request events found in trace.",
                "To capture HTTP events, re-collect with one of the following:",
                "dotnet-trace:\n" +
                "  dotnet-trace collect --profile asp.net\n" +
                "  dotnet-trace collect --providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5'  (ASP.NET Core)\n" +
                "  dotnet-trace collect --providers 'Microsoft-Windows-ASPNET:0xFFFF:5'      (IIS/ASPX)\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:Default /NetworkCapture /NoGui collect");
            return;
        }

        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
            ("Total requests",    data.TotalRequests.ToString("N0")),
            ("Error requests",    $"{data.ErrorCount:N0} ({(data.TotalRequests > 0 ? data.ErrorCount * 100.0 / data.TotalRequests : 0):F1}%)"),
            ("Slow requests",     $"{data.SlowRequestCount:N0} (≥ {data.SlowThresholdMs:F0} ms)"),
            ("Avg latency",       $"{data.AvgRequestMs:F1} ms"),
            ("Max latency",       $"{data.MaxRequestMs:F1} ms"),
            ("P95 latency",       $"{data.P95RequestMs:F1} ms"),
            ("P99 latency",       $"{data.P99RequestMs:F1} ms"),
        ]);

        // Latency gauges
        sink.Gauges([
            ("P95 latency", Math.Min(data.P95RequestMs, 5000), "ms"),
            ("P99 latency", Math.Min(data.P99RequestMs, 5000), "ms"),
        ], barMax: 5000.0);

        // Alerts
        double errorPct = data.TotalRequests > 0 ? data.ErrorCount * 100.0 / data.TotalRequests : 0;
        if (errorPct >= 10)
            sink.Alert(AlertLevel.Critical,
                $"High HTTP error rate: {errorPct:F1}% of requests returned 4xx/5xx.",
                "Check the Exceptions Trace chapter for correlated exception types. " +
                "Cross-reference error timestamps with slow request entries below.");
        else if (errorPct >= 2)
            sink.Alert(AlertLevel.Warning,
                $"HTTP error rate: {errorPct:F1}% ({data.ErrorCount:N0} requests).",
                "Investigate 5xx errors first — these indicate server-side failures.");

        if (data.P99RequestMs >= 5000)
            sink.Alert(AlertLevel.Critical,
                $"P99 latency is {data.P99RequestMs:F0} ms — 1% of requests take over 5 seconds.",
                "This is likely causing user-visible timeouts. Correlate with GC pauses and lock contention in other trace chapters.");
        else if (data.P95RequestMs >= 2000)
            sink.Alert(AlertLevel.Warning,
                $"P95 latency is {data.P95RequestMs:F0} ms — requests are taking over 2 seconds at the 95th percentile.",
                "Check for GC pause spikes in the GC chapter and lock waits in the Contention chapter.");

        // ── Status summary ─────────────────────────────────────────────────────
        if (data.StatusSummary.Count > 0)
        {
            sink.Section("By Status Code", "http-status");

            // Donut: status code distribution
            var statusSegs = data.StatusSummary
                .Select(s => (Label: s.StatusCode.ToString(), Value: (double)s.Count))
                .ToList();
            if (statusSegs.Count > 0)
                sink.DonutChart(statusSegs, "Request count by status code",
                    $"{data.TotalRequests:N0}\nreq");

            var statusRows = data.StatusSummary.Select(s => new[]
            {
                s.StatusCode.ToString(),
                StatusCategory(s.StatusCode),
                s.Count.ToString("N0"),
                $"{s.Count * 100.0 / data.TotalRequests:F1}%",
                $"{s.AvgDurationMs:F1} ms",
            }).ToList();
            sink.Table(
                ["Status", "Category", "Count", "% of total", "Avg latency"],
                statusRows,
                $"HTTP response code distribution across {data.TotalRequests:N0} requests");
        }

        // ── Top paths ──────────────────────────────────────────────────────────
        if (data.TopPaths.Count > 0)
        {
            sink.Section("Top Endpoints", "http-paths");

            // Stacked bar: avg latency by path
            var pathLatencySegs = data.TopPaths.Take(8)
                .Select(p => {
                    string lbl = p.Path.Length > 35 ? "…" + p.Path[^34..] : p.Path;
                    return (Label: lbl, Value: p.AvgDurationMs);
                })
                .ToList();
            if (pathLatencySegs.Count > 0)
                sink.StackedBar(pathLatencySegs, " ms", "Average latency by endpoint (top 8)");

            var pathRows = data.TopPaths.Take(top).Select(p => new[]
            {
                p.Path,
                p.Count.ToString("N0"),
                $"{p.AvgDurationMs:F1} ms",
                $"{p.MaxDurationMs:F0} ms",
                p.ErrorCount > 0 ? p.ErrorCount.ToString("N0") : "—",
            }).ToList();
            sink.Table(
                ["Path", "Requests", "Avg latency", "Max latency", "Errors"],
                pathRows,
                $"Top {pathRows.Count} endpoints by request count");
        }

        // ── Slow requests ──────────────────────────────────────────────────────
        if (data.SlowRequests.Count > 0)
        {
            sink.Section($"Slow Requests (≥ {data.SlowThresholdMs:F0} ms)", "http-slow");

            var slowRows = data.SlowRequests.Take(top).Select(r => new[]
            {
                r.Method,
                r.Path.Length > 70 ? r.Path[..67] + "…" : r.Path,
                r.StatusCode.ToString(),
                $"{r.DurationMs:F0} ms",
                $"{r.StartTimeMs:F0} ms",
            }).ToList();
            sink.Table(
                ["Method", "Path", "Status", "Duration", "Start (trace ms)"],
                slowRows,
                $"{data.SlowRequestCount:N0} requests exceeded {data.SlowThresholdMs:F0} ms — showing slowest {slowRows.Count}");
        }
    }

    private static string StatusCategory(int code) => code switch
    {
        < 200 => "Informational",
        < 300 => "Success",
        < 400 => "Redirect",
        < 500 => "Client Error",
        _     => "Server Error"
    };
}
