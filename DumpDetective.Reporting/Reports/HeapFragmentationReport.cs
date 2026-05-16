using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HeapFragmentationReport
{
    public void Render(HeapFragmentationData data, IRenderSink sink)
    {
        long totalCommitted = data.Segments.Sum(s => s.CommittedBytes);
        long totalFree      = data.Segments.Sum(s => s.FreeBytes);
        double totalFrag    = totalCommitted > 0 ? totalFree * 100.0 / totalCommitted : 0;

        RenderOverall(data, sink, totalCommitted, totalFree, totalFrag);
        RenderSegmentTable(data, sink);
        RenderSegmentAlerts(data, sink, totalFrag);
        RenderFreeDistribution(data, sink);
    }

    private static void RenderOverall(HeapFragmentationData data, IRenderSink sink,
        long totalCommitted, long totalFree, double totalFrag)
    {
        sink.Section("Overall Fragmentation");
        sink.Explain(
            what: "Heap fragmentation is free space scattered between live objects in the managed heap. " +
                  "Fragmentation is measured as the percentage of committed heap memory that is free space " +
                  "but cannot be used for new allocations because it is not contiguous.",
            why:  "High fragmentation forces the GC to either compact the heap (expensive, causes pauses) " +
                  "or trigger more frequent GC collections to consolidate free space. " +
                  "Pinned objects are the most common cause — they prevent the GC from moving live objects during compaction.",
            bullets:
            [
                "Fragmentation > 40% → critical: allocation efficiency is severely impacted",
                "High pinned count per segment → native interop buffers preventing heap compaction",
                "LOH always has high fragmentation → normal behavior, but growing LOH with high fragmentation is a problem",
                "Many free regions of small size → severe fragmentation causing frequent OOM despite available total free",
            ],
            impact: "Severe fragmentation can cause OutOfMemoryException even when total heap free space appears adequate. " +
                    "The GC may be unable to find a contiguous block large enough for a requested allocation.",
            action: "Run 'pinned-objects <dump>' to see what is pinned and where. " +
                    "Consider ArrayPool<T> and MemoryPool<T> for I/O buffers to avoid pinning large arrays.");
        sink.KeyValues([
            ("Total committed", Fmt(totalCommitted)),
            ("Total live",      Fmt(data.Segments.Sum(s => s.LiveBytes))),
            ("Total free",      Fmt(totalFree)),
            ("Overall frag %",  $"{totalFrag:F1}%"),
            ("Total pinned",    data.Segments.Sum(s => s.PinnedCount).ToString("N0")),
        ]);

        // Per-segment frag % gauges — most fragmented segments first
        var fragGauges = data.Segments
            .Where(s => s.CommittedBytes > 0)
            .Select(s => ($"0x{s.Address:X} ({s.Kind})", s.FreeBytes * 100.0 / s.CommittedBytes, "%"))
            .OrderByDescending(t => t.Item2)
            .Take(8)
            .ToList();
        if (fragGauges.Count > 0)
            sink.Gauges(fragGauges, barMax: 100.0);

        // Live vs. free composition — stacked bar
        if (totalCommitted > 0)
        {
            long totalLive = data.Segments.Sum(s => s.LiveBytes);
            var compSegs = new List<(string, double)>();
            if (totalLive > 0) compSegs.Add(("Live", (double)totalLive));
            if (totalFree > 0) compSegs.Add(("Free (fragmented)", (double)totalFree));
            if (compSegs.Count > 1)
                sink.StackedBar(compSegs, null, "Committed heap: live vs. fragmented free", valueMode: "size");
        }

        if (totalFrag >= 40)
            sink.Alert(AlertLevel.Critical, $"Heap fragmentation critical: {totalFrag:F1}%",
                advice: "Reduce GCHandle.Alloc(Pinned) usage. Use MemoryPool<T> / ArrayPool<T> for I/O buffers. Enable Server GC for large workloads.");
        else if (totalFrag >= 20)
            sink.Alert(AlertLevel.Warning, $"Heap fragmentation elevated: {totalFrag:F1}%");
    }

    private static void RenderSegmentTable(HeapFragmentationData data, IRenderSink sink)
    {
        var rows = data.Segments.Select(s =>
        {
            double frag = s.CommittedBytes > 0 ? s.FreeBytes * 100.0 / s.CommittedBytes : 0;
            return new[]
            {
                $"0x{s.Address:X}",
                s.Kind,
                Fmt(s.CommittedBytes),
                Fmt(s.LiveBytes),
                Fmt(s.FreeBytes),
                $"{frag:F1}%",
                s.PinnedCount.ToString("N0"),
            };
        }).ToList();

        sink.BeginDetails($"Segment Details ({data.Segments.Count} segment(s))");
        sink.Table(["Segment Addr", "Kind", "Committed", "Live", "Free", "Frag %", "Pinned"], rows);
        sink.EndDetails();
    }

    private static void RenderSegmentAlerts(HeapFragmentationData data, IRenderSink sink, double totalFrag)
    {
        var highFrag    = new List<string>();
        var pinnedFrag  = new List<string>();

        foreach (var s in data.Segments)
        {
            if (s.CommittedBytes <= 0) continue;
            double frag = s.FreeBytes * 100.0 / s.CommittedBytes;
            if (frag < 50) continue;

            string entry = $"0x{s.Address:X} ({s.Kind}){(s.PinnedCount > 0 ? $" — {s.PinnedCount:N0} pinned" : "")}";
            if (s.PinnedCount > 0)
                pinnedFrag.Add(entry);
            else
                highFrag.Add(entry);
        }

        if (pinnedFrag.Count > 0)
            sink.Alert(AlertLevel.Warning,
                $"{pinnedFrag.Count} segment{(pinnedFrag.Count == 1 ? " has" : "s have")} pinned objects limiting compaction",
                advice: "Pinned objects prevent the GC from moving live objects during compaction. " +
                        "Minimise GCHandle.Alloc(Pinned) lifetime. See segment table for details.");

        if (highFrag.Count > 0)
        {
            // Split segments into ephemeral/young (Gen0, Gen1, Ephemeral) vs old (Gen2, LOH, etc.).
            // Ephemeral segments are partially empty by design — the GC reserves committed space
            // for future promotions — so high free % there is normal and not a concern.
            bool anyNonEphemeral = data.Segments.Any(s =>
            {
                if (s.CommittedBytes <= 0) return false;
                double f = s.FreeBytes * 100.0 / s.CommittedBytes;
                return f >= 50 && s.PinnedCount == 0 &&
                       s.Kind is not ("Gen0" or "Gen1" or "Ephemeral");
            });

            string overallNote = totalFrag < 20
                ? $"Overall heap fragmentation is only {totalFrag:F1}% — " +
                  (anyNonEphemeral
                      ? "most fragmented segments are Gen2 or LOH; watch for growth over time."
                      : "all fragmented segments are ephemeral/young-gen regions, which is expected and not a concern.")
                : $"Overall heap fragmentation is {totalFrag:F1}%. " +
                  (anyNonEphemeral
                      ? "Gen2/LOH segments are fragmented — this is worth investigating."
                      : "All fragmented segments are ephemeral regions; check Gen2/LOH in the segment table.");

            sink.Alert(totalFrag >= 20 && anyNonEphemeral ? AlertLevel.Warning : AlertLevel.Info,
                $"{highFrag.Count} segment{(highFrag.Count == 1 ? " is" : "s are")} mostly free",
                advice: overallNote +
                        " Consider GC.Collect(2, GCCollectionMode.Aggressive) if fragmentation is growing over time.");
        }
    }

    private static void RenderFreeDistribution(HeapFragmentationData data, IRenderSink sink)
    {
        if (data.FreeDistribution.Count == 0) return;
        sink.Section("Free Object (Holes) Distribution");

        var rows = data.FreeDistribution
            .OrderBy(b => b.SortKey)
            .Select(b => new[] { b.Label, b.Count.ToString("N0"), Fmt(b.TotalBytes) })
            .ToList();

        int largeFreeCount = data.FreeDistribution.Where(b => b.SortKey >= 4).Sum(b => (int)b.Count);
        sink.Table(["Free Hole Size", "Count", "Total Size"], rows,
            "Smaller/more-numerous holes = harder to compact");

        if (largeFreeCount > 10)
            sink.Alert(AlertLevel.Warning,
                $"{largeFreeCount} free holes ≥ 64 KB — large gaps can be re-used by LOH allocations.",
                "Large free holes often indicate recently freed large arrays or strings.");
    }

    private static string Fmt(long b) => DumpHelpers.FormatSize(b);
}
