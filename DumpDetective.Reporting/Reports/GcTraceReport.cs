using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class GcTraceReport
{
    public void Render(GcTraceData data, IRenderSink sink, int top = 30)
    {
        sink.Explain(
            what: "GC pause analysis — per-collection pause times, trigger reasons, and heap sizes from a trace file.",
            why: "GC pauses stop application threads. Gen 2 and blocking GCs cause the longest pauses. Frequent induced or LOH GCs indicate allocation or memory pressure problems.",
            impact: "High max pause or many Gen 2 GCs directly affect application latency. Background GCs are typically cheaper.",
            bullets: [
                "Gen 0 GCs — fast, frequent (allocation pressure on SOH)",
                "Gen 1 GCs — moderate cost, promote surviving objects to Gen 2",
                "Gen 2 GCs — most expensive; blocking Gen 2 = full stop-the-world",
                "Reason = AllocSmall/AllocLarge → GC triggered by normal allocation pressure",
                "Reason = Induced → GC.Collect() called explicitly — often an anti-pattern"
            ],
            action: "Investigate Gen 2 blocking GCs with long pauses. If Induced reason appears frequently, find the GC.Collect() call site. LOH allocations (AllocLarge) trigger Gen 2 — consider LOH pooling."
        );

        sink.Section("Trace Summary", "gc-summary");
        sink.KeyValues([
            ("Trace",           data.TraceInfo),
            ("Total GCs",       data.TotalGcs.ToString("N0")),
            ("Total pause",     $"{data.TotalPauseMs:F1} ms"),
            ("Max pause",       $"{data.MaxPauseMs:F1} ms"),
            ("Avg pause",       $"{data.AvgPauseMs:F1} ms"),
            ("Process filter",  data.FilteredProcess ?? "(all processes)"),
        ]);

        if (data.TotalGcs == 0)
        {
            sink.Alert(AlertLevel.Warning, "No GC events found in trace.",
                "To capture GC events, re-collect with one of the following:",
                "dotnet-trace:\n" +
                "  dotnet-trace collect --profile gc-verbose\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:GC,GCHeapSurvivalAndMovement,GCHeapAndTypeNames,Default /NoGui collect");
            return;
        }

        // Gen summary table
        sink.Section("By Generation", "gc-by-gen");
        // GC count by generation — donut
        var genCountSegs = data.GenSummary
            .Select(g => (Label: g.Generation == 3 ? "LOH/Gen3" : $"Gen {g.Generation}",
                          Value: (double)g.Count))
            .ToList();
        if (genCountSegs.Count > 0)
            sink.DonutChart(genCountSegs, "GC count distribution by generation (not pause time)", $"{data.TotalGcs}\nGCs");

        // Total pause by generation — stacked bar
        var genPauseSegs = data.GenSummary
            .Select(g => (Label: g.Generation == 3 ? "LOH/Gen3" : $"Gen {g.Generation}",
                          Value: g.TotalPauseMs))
            .ToList();
        if (genPauseSegs.Count > 0)
            sink.StackedBar(genPauseSegs, " ms", "Total pause time by generation");

        var genRows = data.GenSummary.Select(g => new[]
        {
            g.Generation == 3 ? "LOH/Gen3" : $"Gen {g.Generation}",
            g.Count.ToString("N0"),
            $"{g.TotalPauseMs:F1} ms",
            $"{g.MaxPauseMs:F1} ms",
            $"{g.AvgPauseMs:F1} ms",
        }).ToList();
        sink.Table(["Generation", "Count", "Total pause", "Max pause", "Avg pause"], genRows,
            $"{data.TotalGcs:N0} total GCs across all generations");

        // Top pauses
        sink.Section("Longest Pauses", "gc-top-pauses");
        var pauseRows = data.TopPauses.Take(top).Select(p => new[]
        {
            p.GcIndex.ToString("N0"),
            p.Generation == 3 ? "LOH/Gen3" : $"Gen {p.Generation}",
            p.Reason,
            p.Type,
            $"{p.PauseMs:F1} ms",
            p.HeapSizeBefore > 0 ? DumpHelpers.FormatSize(p.HeapSizeBefore) : "—",
            p.HeapSizeAfter  > 0 ? DumpHelpers.FormatSize(p.HeapSizeAfter)  : "—",
        }).ToList();
        // GC timelines — pause duration + heap size on the same axis so growth→pause correlation is visible
        {
            var pauseTimeline = data.Events.Count > 1
                ? data.Events.Select(e => e.PauseMs).ToList()
                : null;
            var heapSizes = data.Events.Where(e => e.HeapSizeAfter > 0)
                                        .Select(e => e.HeapSizeAfter / (1024.0 * 1024.0)).ToList();

            var series = new List<(string Label, IReadOnlyList<double> Values, string? Unit)>();
            if (pauseTimeline is { Count: > 1 })
                series.Add(("Pause (ms/collection)", pauseTimeline, " ms"));
            if (heapSizes.Count > 1)
                series.Add(("Heap after GC (MB)", heapSizes, " MB"));

            if (series.Count == 2)
                sink.MultiSparkline(series,
                    caption: "Aligned per-collection timeline — rising heap sizes typically precede longer Gen2 pauses");
            else if (pauseTimeline is { Count: > 1 })
                sink.Sparkline(pauseTimeline, "GC pause timeline (ms per collection)", " ms");
            else if (heapSizes.Count > 1)
                sink.Sparkline(heapSizes, "Heap size after each GC (MB)", " MB");
        }

        sink.Table(
            ["GC #", "Gen", "Reason", "Type", "Pause", "Heap before", "Heap after"],
            pauseRows,
            $"Top {pauseRows.Count} longest GC pauses (out of {data.TotalGcs:N0} total)");

        // Alert on long blocking GC pauses
        var blocking = data.TopPauses.Where(p => p.Type == "Blocking" && p.PauseMs > 100).ToList();
        if (blocking.Count > 0)
        {
            sink.Alert(AlertLevel.Critical,
                $"{blocking.Count} blocking GC pause(s) > 100 ms detected — application threads were frozen.",
                $"Longest: {blocking[0].PauseMs:F1} ms (Gen {blocking[0].Generation}, {blocking[0].Reason})",
                "Review LOH usage, pinned objects, and large object allocations.");
        }

        var induced = data.TopPauses.Where(p => p.Reason == "Induced").ToList();
        if (induced.Count > 0)
        {
            sink.Alert(AlertLevel.Warning,
                $"{induced.Count} Induced GC(s) detected — GC.Collect() was called explicitly.",
                "Explicit GC.Collect() calls are generally an anti-pattern and can cause performance issues.",
                "Search for GC.Collect() / GC.Collect(int) calls in the application code and remove or conditionalise them.");
        }
    }
}
