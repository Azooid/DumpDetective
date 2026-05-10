using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class LohTraceReport
{
    public void Render(LohTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "Large Object Heap (LOH) size trend analysis from GCHeapStats events — tracks LOH size across GC collections to detect steady growth and fragmentation.",
            why: "The LOH is only compacted when explicitly triggered. Uncontrolled LOH growth causes Gen2 fragmentation and increases Full GC frequency.",
            impact: "Every 85 KB+ allocation goes to the LOH. Large arrays and strings allocated frequently fragment the LOH, causing long Gen2 pauses.",
            bullets: [
                "LOH growth          — bytes gained from start to end of trace",
                "Trend direction     — whether LOH is consistently growing",
                "Gen2 GCs            — count of Gen2 collections involving LOH growth"
            ],
            action: "Use ArrayPool<byte> and MemoryPool<T> for large buffers. " +
                    "Enable LOH compaction: GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce. " +
                    "Profile large allocations with 'alloc-trace'."
        );

        sink.Section("Trace Summary", "loh-summary");
        sink.KeyValues([
            ("Trace",              data.TraceInfo),
            ("Start LOH size",     $"{data.StartLohBytes / 1024 / 1024:N1} MB"),
            ("Peak LOH size",      $"{data.PeakLohBytes  / 1024 / 1024:N1} MB"),
            ("End LOH size",       $"{data.EndLohBytes   / 1024 / 1024:N1} MB"),
            ("Net LOH growth",     $"{data.LohGrowthBytes / 1024 / 1024:N1} MB"),
            ("Total GCs",          data.TotalGcCount.ToString("N0")),
            ("Gen2 with LOH growth",data.Gen2GcsWithLohGrowth.ToString("N0")),
            ("Trending up",        data.IsTrendingUp ? "Yes ⚠" : "No"),
            ("Process filter",     data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No GCHeapStats events found.",
                "Collect with: --providers 'Microsoft-Windows-DotNETRuntime:0x1:4' (GCKeyword)");
            return;
        }

        if (data.IsTrendingUp)
            sink.Alert(AlertLevel.Warning,
                $"LOH is trending upward — grew {data.LohGrowthBytes / 1024 / 1024:N1} MB during this trace.",
                "Consistent LOH growth indicates large objects are not being reclaimed.",
                "Identify large allocations with 'alloc-trace' or a heap dump.");

        if (data.LohSizeTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "LOH size over time (MB)", " MB");
    }
}
