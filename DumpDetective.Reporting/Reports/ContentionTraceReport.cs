using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class ContentionTraceReport
{
    public void Render(ContentionTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Lock contention analysis — ContentionStart/Stop event pairs showing where threads waited on monitors, mutexes, or other synchronisation primitives.",
            why: "Contention occurs when multiple threads compete for the same lock. High wait times or high contention counts indicate synchronisation bottlenecks that limit throughput.",
            impact: "Contention reduces concurrency, increases latency, and can lead to thread-pool starvation under load. The hottest contended call site is typically the most actionable finding.",
            bullets: [
                "Total wait time — total time threads spent waiting for locks across the trace",
                "Max wait time  — the single worst lock acquisition delay",
                "Hotspot        — the call site with the highest total accumulated wait time"
            ],
            action: "Narrow hot locks to shorter critical sections. Consider lock-free structures, ConcurrentDictionary, or splitting a single shared lock into per-bucket locks."
        );

        sink.Section("Trace Summary", "contention-summary");
        sink.KeyValues([
            ("Trace",            data.TraceInfo),
            ("Total contentions", data.TotalContentions.ToString("N0")),
            ("Total wait time",  $"{data.TotalWaitMs:F1} ms"),
            ("Max wait",         $"{data.MaxWaitMs:F1} ms"),
            ("Avg wait",         $"{data.AvgWaitMs:F2} ms"),
            ("Threads affected", data.ThreadsAffected.ToString("N0")),
            ("Process filter",   data.FilteredProcess ?? "(all processes)"),
        ]);

        if (data.TotalContentions == 0)
        {
            sink.Alert(AlertLevel.Warning, "No contention events found in trace.",
                "Collect with contention events enabled.",
                "dotnet-trace: add --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4'\nPerfView: check 'ContentionStacks' in providers");
            return;
        }

        if (data.MaxWaitMs > 500)
            sink.Alert(AlertLevel.Critical,
                $"Severe contention detected: max wait {data.MaxWaitMs:F1} ms.",
                "At least one thread waited > 500 ms for a lock — this is a major latency contributor.",
                "Profile under realistic load and consider redesigning the hot lock.");
        else if (data.TotalWaitMs > 1000)
            sink.Alert(AlertLevel.Warning,
                $"Significant lock contention: {data.TotalWaitMs:F1} ms total wait.",
                "Contention is measurable and worth optimising at scale.",
                "Review the top hotspots below.");

        // Top hotspots
        sink.Section("Top Contention Hotspots", "contention-hotspots");
        var hRows = data.Hotspots.Take(top).Select(h => new[]
        {
            TrimFrame(h.Location, 90),
            h.Count.ToString("N0"),
            $"{h.TotalWaitMs:F1} ms",
            $"{h.MaxWaitMs:F1} ms",
        }).ToList();
        sink.Table(
            ["Call site", "Count", "Total wait", "Max wait"],
            hRows,
            $"Top {hRows.Count} hotspots by total wait time");

        // Top individual events
        sink.Section("Worst Individual Waits", "contention-events");
        var evRows = data.Events.Take(top).Select(e => new[]
        {
            $"{e.WaitMs:F2} ms",
            e.ThreadId.ToString(),
            $"{e.TimeMs:F3} ms",
            TrimFrame(e.TopFrame, 80),
        }).ToList();
        sink.Table(
            ["Wait", "Thread", "Time in trace", "Call site"],
            evRows,
            $"Top {evRows.Count} individual contention events by wait duration");
    }

    private static string TrimFrame(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
