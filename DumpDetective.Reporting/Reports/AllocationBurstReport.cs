using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class AllocationBurstReport
{
    public void Render(AllocationBurstData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Allocation burst detection from GCAllocationTick events — identifies periods where the allocation rate spikes to 3× or more above the rolling median.",
            why: "Allocation bursts stress the GC nursery and force early promotions to Gen1/Gen2, increasing GC pause frequency and duration.",
            impact: "A 5-second allocation burst at 50 MB/s generates 250 MB of garbage, likely triggering multiple Gen1 and one Gen2 collection.",
            bullets: [
                "Burst periods       — time windows with anomalous allocation rate",
                "Peak rate           — highest allocation rate observed in any window",
                "Rate timeline       — per-second sparkline of allocation throughput"
            ],
            action: "Use dotnet-trace with GCAllocationTick to find which types are allocated during bursts. " +
                    "Target the largest burst periods first. Common causes: LINQ chaining, string concatenation, boxing in hot paths."
        );

        sink.Section("Trace Summary", "alloc-burst-summary");
        string FmtRate(double kbps) => kbps >= 1024 ? $"{kbps / 1024.0:F1} MB/s" : $"{kbps:F0} KB/s";
        sink.KeyValues([
            ("Trace",               data.TraceInfo),
            ("Burst count",         data.BurstCount.ToString("N0")),
            ("Peak burst rate",     FmtRate(data.PeakBurstRateKbPerSec)),
            ("Avg allocation rate", FmtRate(data.AvgAllocationRateKbPerSec)),
            ("Process filter",      data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No allocation burst periods detected.",
                "No 500 ms window reached 3× the rolling median rate.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x1:4' (GCKeyword)");
            return;
        }

        if (data.BurstCount >= 5)
            sink.Alert(AlertLevel.Warning,
                $"{data.BurstCount} allocation burst(s) detected.",
                "Repeated allocation spikes will pressure the GC and increase pause durations.",
                "Profile with 'alloc-trace' to identify the allocating call sites.");

        if (data.RateTimeline is { Count: > 2 } tl)
        {
            // Auto-scale sparkline unit: show MB/s when peak > 1 MB/s
            bool useMb = tl.Any(v => v >= 1024);
            var  tl2   = useMb ? tl.Select(v => v / 1024.0).ToList() : tl;
            sink.Sparkline(tl2, useMb ? "Allocation rate over time (MB/s)" : "Allocation rate over time (KB/s)",
                           useMb ? " MB/s" : " KB/s");
        }

        if (data.BurstPeriods.Count > 0)
        {
            sink.Section("Burst Periods", "alloc-burst-periods");
            var rows = new List<string[]>(Math.Min(top, data.BurstPeriods.Count));
            foreach (var b in data.BurstPeriods.Take(top))
            {
                long durMs    = (long)(b.EndMs - b.StartMs);
                string start  = b.StartMs >= 60_000 ? $"{b.StartMs / 60_000.0:F1} min"
                              : b.StartMs >= 1_000   ? $"{b.StartMs / 1_000.0:F1} s"
                              :                        $"{b.StartMs:F0} ms";
                string dur    = durMs    >= 1_000 ? $"{durMs / 1_000.0:F1} s" : $"{durMs} ms";
                string rate   = b.PeakRateKbPerSec >= 1024
                              ? $"{b.PeakRateKbPerSec / 1024.0:F1} MB/s"
                              : $"{b.PeakRateKbPerSec:F0} KB/s";
                string bytes  = b.EstimatedBytes >= 1024 * 1024
                              ? $"{b.EstimatedBytes / (1024.0 * 1024.0):F1} MB"
                              : $"{b.EstimatedBytes / 1024.0:F1} KB";
                rows.Add([start, dur, rate, bytes, b.TopType]);
            }
            sink.Table(
                ["Start Offset", "Duration", "Peak Rate", "Estimated Bytes", "Top Type"],
                rows, "Burst periods ordered by peak allocation rate descending");
        }
    }
}
