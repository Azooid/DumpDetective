using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class SocketTraceReport
{
    public void Render(SocketTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Socket connect analysis from System.Net.Sockets EventSource — measures connection latency, detects failures, and identifies slow or failing remote endpoints.",
            why: "High socket connect latency adds to request tail latency and, when connect fails, triggers expensive retry logic and thread pool exhaustion.",
            impact: "A connection timeout of 30 seconds blocks a thread pool thread for the full duration. Under load this quickly exhausts the pool.",
            bullets: [
                "Connect failures    — sockets that failed to connect (ECONNREFUSED, timeout, etc.)",
                "Slow connects       — connections exceeding 500 ms",
                "Top hosts           — remote endpoints with highest connection volume"
            ],
            action: "Set aggressive connect timeouts (< 5 s) to fail fast. " +
                    "Cache and reuse HttpClient/SocketsHttpHandler for HTTP connections. " +
                    "Use connection pools for raw socket clients."
        );

        sink.Section("Trace Summary", "socket-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total connects",     data.TotalConnects.ToString("N0")),
            ("Connect failures",   data.TotalConnectFailed > 0
                                    ? $"{data.TotalConnectFailed} ⚠" : "0"),
            ("Avg connect time",   $"{data.AvgConnectMs:F1} ms"),
            ("Max connect time",   $"{data.MaxConnectMs:F1} ms"),
            ("Slow connects",      data.SlowConnectCount.ToString("N0")),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No socket events found.",
                "Collect with: --providers 'System.Net.Sockets:0xFF:5'");
            return;
        }

        if (data.TotalConnectFailed > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalConnectFailed} socket connection failure(s) detected.",
                "Connection failures cause retry overhead and user-visible errors.",
                "Check remote endpoint availability and firewall rules.");

        if (data.MaxConnectMs > 5000)
            sink.Alert(AlertLevel.Warning,
                $"Very slow socket connect detected: {data.MaxConnectMs:F0} ms.",
                "A 5+ second connect blocks the calling thread for the full duration.");

        if (data.ConnectTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "Socket connect attempts per second", "/s");

        if (data.SlowConnects.Count > 0)
        {
            sink.Section("Slow / Failed Connects", "socket-slow");
            var rows = new List<string[]>(Math.Min(top, data.SlowConnects.Count));
            foreach (var c in data.SlowConnects.Take(top))
                rows.Add([c.RemoteEndpoint, c.Failed ? "FAILED" : $"{c.DurationMs:F0} ms",
                           $"{c.TimeMs:F0} ms"]);
            sink.Table(
                ["Endpoint", "Result / Duration", "Start Offset"],
                rows, "Ordered by duration descending");
        }

        if (data.TopHosts.Count > 0)
        {
            sink.Section("Top Remote Hosts", "socket-hosts");
            var rows = new List<string[]>(Math.Min(top, data.TopHosts.Count));
            foreach (var h in data.TopHosts.Take(top))
                rows.Add([h.Host, h.ConnectCount.ToString("N0"), h.FailureCount.ToString("N0"),
                           $"{h.AvgMs:F1} ms", $"{h.TotalMs:F0} ms"]);
            sink.Table(
                ["Host", "Connects", "Failures", "Avg Time", "Total Time"],
                rows, "Ordered by connect count descending");
        }
    }
}
