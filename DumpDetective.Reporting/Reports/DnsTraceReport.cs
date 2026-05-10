using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class DnsTraceReport
{
    public void Render(DnsTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "DNS resolution analysis from System.Net.NameResolution events — measures lookup latency, detects failure storms, and identifies frequently resolved hostnames.",
            why: "DNS resolutions missing the local cache can take 50–500 ms per lookup. Under high concurrency or misconfigured TTLs, this adds significant latency to every outbound request.",
            impact: "An application resolving the same hostname 1000 times/minute with a 200 ms average DNS RTT wastes 200 seconds of thread time per minute.",
            bullets: [
                "Resolution failures — failed DNS lookups by hostname",
                "Slow resolutions    — lookups exceeding 100 ms",
                "Top hostnames       — most frequently resolved names (possible cache misses)"
            ],
            action: "Set appropriate DNS TTLs (300–3600 s). " +
                    "Use SocketsHttpHandler.PooledConnectionLifetime to limit re-resolution frequency in HttpClient. " +
                    "Consider a local DNS caching daemon for containerized workloads."
        );

        sink.Section("Trace Summary", "dns-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Total resolutions",  data.TotalResolutions.ToString("N0")),
            ("Failed resolutions", data.TotalFailed > 0
                                    ? $"{data.TotalFailed} ⚠" : "0"),
            ("Avg resolution time",$"{data.AvgResolutionMs:F1} ms"),
            ("Max resolution time",$"{data.MaxResolutionMs:F1} ms"),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No DNS resolution events found.",
                "Collect with: --providers 'System.Net.NameResolution:0xFF:5'");
            return;
        }

        if (data.TotalFailed > 0)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalFailed} DNS resolution failure(s) detected.",
                "Failed DNS lookups cause request errors and may trigger retry storms.",
                "Verify the hostnames are resolvable from this host and check /etc/hosts and DNS server settings.");

        if (data.MaxResolutionMs > 1000)
            sink.Alert(AlertLevel.Warning,
                $"Very slow DNS lookup: {data.MaxResolutionMs:F0} ms.",
                "DNS resolution blocking the calling thread for >1 second will cause latency spikes.");

        if (data.FailureTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "DNS failures per second", "/s");

        if (data.TopHostnames.Count > 0)
        {
            sink.Section("Top Resolved Hostnames", "dns-hosts");
            var rows = new List<string[]>(Math.Min(top, data.TopHostnames.Count));
            foreach (var h in data.TopHostnames.Take(top))
                rows.Add([h.Hostname, h.Count.ToString("N0"), h.FailureCount.ToString("N0"),
                           $"{h.AvgMs:F1} ms", $"{h.MaxMs:F1} ms", $"{h.TotalMs:F0} ms"]);
            sink.Table(
                ["Hostname", "Resolutions", "Failures", "Avg Time", "Max Time", "Total Time"],
                rows, "Ordered by resolution count");
        }

        if (data.SlowResolutions.Count > 0)
        {
            sink.Section("Slow / Failed Resolutions", "dns-slow");
            var rows = new List<string[]>(Math.Min(top, data.SlowResolutions.Count));
            foreach (var r in data.SlowResolutions.Take(top))
                rows.Add([r.Hostname, r.Failed ? "FAILED" : $"{r.DurationMs:F0} ms",
                           $"{r.TimeMs:F0} ms"]);
            sink.Table(
                ["Hostname", "Result / Duration", "Start Offset"],
                rows, "Ordered by duration descending");
        }
    }
}
