using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class MemoryPressureReport
{
    public void Render(MemoryPressureData data, IRenderSink sink)
    {
        sink.Section("Memory Pressure Correlation");
        sink.Explain(
            what: "A unified view of managed memory at the time of the dump: heap committed and reserved bytes " +
                  "by generation, heap fragmentation, and thread stack memory.",
            why:  "Managed heap analysis alone can miss the full picture. Thread stacks, native interop buffers, " +
                  "and GC-reserved-but-uncommitted memory all consume virtual address space and can contribute " +
                  "to OutOfMemoryException even when the managed heap appears healthy.",
            bullets:
            [
                "Heap Committed > Heap Live → fragmentation: memory is reserved for the GC but not filled",
                "Heap Reserved >> Heap Committed → GC pre-reserved address space; may restrict native allocations",
                "Thread stacks > 200 MB → too many threads (200+ threads × 1 MB stack = 200 MB)",
                "Gen2 dominating committed → long-lived or leaked objects preventing GC collection",
            ],
            impact: "High virtual address space consumption (committed + reserved) limits native memory allocations " +
                    "and can cause OutOfMemoryException even when physical memory is available.");

        long totalKnown = data.ManagedHeapCommitted + data.ThreadStacksCommitted;
        static string Pct(long part, long total) =>
            total > 0 ? $" ({part * 100.0 / total:F1}%)" : string.Empty;

        sink.KeyValues([
            ("Managed heap committed",  $"{DumpHelpers.FormatSize(data.ManagedHeapCommitted)}{Pct(data.ManagedHeapCommitted, totalKnown)}"),
            ("Managed heap reserved",   DumpHelpers.FormatSize(data.ManagedHeapReserved)),
            ("Managed heap live",       $"{DumpHelpers.FormatSize(data.ManagedHeapLive)}{Pct(data.ManagedHeapLive, data.ManagedHeapCommitted)}"),
            ("Managed heap free",       $"{DumpHelpers.FormatSize(data.ManagedHeapFree)}{Pct(data.ManagedHeapFree, data.ManagedHeapCommitted)}"),
            ("Thread stacks committed", $"{DumpHelpers.FormatSize(data.ThreadStacksCommitted)}{Pct(data.ThreadStacksCommitted, totalKnown)} ({data.ThreadCount:N0} threads)"),
            ("Segment count",           data.SegmentCount.ToString("N0")),
        ]);

        // Stacked bar: where committed memory goes
        {
            var bars = new List<(string Label, double Value)>();
            if (data.Gen0Bytes > 0) bars.Add(("Gen0",  (double)data.Gen0Bytes));
            if (data.Gen1Bytes > 0) bars.Add(("Gen1",  (double)data.Gen1Bytes));
            if (data.Gen2Bytes > 0) bars.Add(("Gen2",  (double)data.Gen2Bytes));
            if (data.LohBytes  > 0) bars.Add(("LOH",   (double)data.LohBytes));
            if (data.PohBytes  > 0) bars.Add(("POH",   (double)data.PohBytes));
            if (data.ManagedHeapFree > 0) bars.Add(("Free/Fragmented", (double)data.ManagedHeapFree));
            if (bars.Count > 1)
                sink.StackedBar(bars, null, "Heap committed bytes breakdown", valueMode: "size");
        }

        // Alerts
        double fragPct = data.ManagedHeapCommitted > 0
            ? data.ManagedHeapFree * 100.0 / data.ManagedHeapCommitted : 0;
        if (fragPct > 40)
            sink.Alert(AlertLevel.Critical,
                $"Heap fragmentation: {fragPct:F1}% of committed heap is free but fragmented.",
                advice: "Run 'heap-fragmentation <dump>' for per-segment analysis. Check for pinned objects.");
        else if (fragPct > 20)
            sink.Alert(AlertLevel.Warning, $"Heap fragmentation: {fragPct:F1}% of committed heap is free.");

        if (data.ThreadStacksCommitted > 200_000_000)
            sink.Alert(AlertLevel.Warning,
                $"Thread stacks commit {DumpHelpers.FormatSize(data.ThreadStacksCommitted)} " +
                $"across {data.ThreadCount:N0} threads.",
                advice: "High thread count increases virtual address pressure. " +
                        "Use async I/O and thread pools instead of dedicated threads.");

        if (data.ManagedHeapReserved > data.ManagedHeapCommitted * 3)
            sink.Alert(AlertLevel.Info,
                $"GC has reserved {DumpHelpers.FormatSize(data.ManagedHeapReserved)} but only committed " +
                $"{DumpHelpers.FormatSize(data.ManagedHeapCommitted)}. " +
                "Reserved space counts against virtual address space limits.");

        // Region summary table
        sink.Section("Heap Segment Summary by Kind");
        var regionRows = data.RegionSummary
            .Select(r =>
            {
                double frag = r.CommittedBytes > 0 ? r.FreeBytes * 100.0 / r.CommittedBytes : 0;
                return new[]
                {
                    r.Kind,
                    r.Count.ToString("N0"),
                    DumpHelpers.FormatSize(r.CommittedBytes),
                    DumpHelpers.FormatSize(r.ReservedBytes),
                    DumpHelpers.FormatSize(r.LiveBytes),
                    $"{frag:F1}%",
                };
            })
            .ToList();
        sink.Table(["Kind", "Segments", "Committed", "Reserved", "Live", "Frag %"], regionRows);
    }
}
