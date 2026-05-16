using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class LargeObjectsReport
{
    public void Render(LargeObjectsData data, IRenderSink sink,
        int top = 50, bool showAddr = false, bool typeBreakdown = false)
    {
        if (data.Objects.Count == 0) { sink.Text($"No objects \u2265 {DumpHelpers.FormatSize(data.MinSize)} found."); return; }

        sink.Explain(
            what: "Large Object Heap (LOH) analysis — objects ≥ 85,000 bytes (default) are allocated directly in the LOH, bypassing Gen0/Gen1.",
            why: "The LOH is only collected during Gen2 GC (an expensive full collection). It is not compacted by default, so free space fragments over time.",
            impact: "LOH fragmentation causes OutOfMemoryException even when total free space appears sufficient. Large array allocations fail first.",
            bullets: ["'Type Aggregate' shows which types dominate LOH — byte[] and string[] are common culprits", "'Segment Breakdown' shows how much of each LOH segment is free vs used", "High fragmentation with large free spans = GC cannot satisfy new large allocations"],
            action: "Pool large buffers with ArrayPool<byte>.Shared or MemoryPool<T> to avoid repeated LOH allocation and fragmentation."
        );
        sink.KeyValues([
            ("Objects found",   data.Objects.Count.ToString("N0")),
            ("Total size",      DumpHelpers.FormatSize(data.TotalSize)),
            ("Largest object",  DumpHelpers.FormatSize(data.Objects[0].Size)),
        ]);

        if (data.TotalSize > 500 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"LOH/Large object total: {DumpHelpers.FormatSize(data.TotalSize)}",
                "Large objects are allocated directly in the LOH which is only collected on Gen2 GC.",
                "Pool large buffers using ArrayPool<byte> or MemoryPool<T> to reduce LOH pressure.");

        RenderLohFreeSpace(sink, data);
        RenderTypeAggregate(sink, data, top);
        if (!typeBreakdown) RenderIndividualObjects(sink, data, top, showAddr);
        RenderSegmentBreakdown(sink, data);
    }

    private static void RenderTypeAggregate(IRenderSink sink, LargeObjectsData data, int top)
    {
        sink.Section("Type Aggregate");
        var typeAgg = data.Objects
            .GroupBy(o => o.Type)
            .Select(g => (Type: g.Key, ElemType: g.First().ElemType, Count: g.Count(), Size: g.Sum(o => o.Size)))
            .OrderByDescending(t => t.Size)
            .Take(top)
            .ToList();

        // Top 8 types by size — donut above the table
        if (typeAgg.Count > 1 && data.TotalSize > 0)
        {
            var segs = typeAgg.Take(8).Select(t => {
                string lbl = t.Type.Contains('.') ? t.Type[(t.Type.LastIndexOf('.')+1)..] : t.Type;
                if (lbl.Length > 28) lbl = lbl[..28] + "\u2026";
                return (Label: lbl, Value: (double)t.Size);
            }).ToList();
            sink.DonutChart(segs, "Top 8 LOH types by size",
                $"{DumpHelpers.FormatSize(data.TotalSize)}\nLOH total");
        }

        var rows = typeAgg.Select(t => new[]
        {
            t.Type,
            t.ElemType.Length > 0 ? t.ElemType : "\u2014",
            t.Count.ToString("N0"),
            DumpHelpers.FormatSize(t.Size),
            data.TotalSize > 0 ? $"{t.Size * 100.0 / data.TotalSize:F1}%" : "?",
        }).ToList();
        sink.Table(["Type", "Element Type", "Count", "Total Size", "% of LOH+"], rows,
            $"Top {rows.Count} types \u2265 {DumpHelpers.FormatSize(data.MinSize)}");
    }

    private static void RenderIndividualObjects(IRenderSink sink, LargeObjectsData data,
        int top, bool showAddr)
    {
        sink.Section($"Top {Math.Min(top, data.Objects.Count)} Largest Individual Objects");
        bool hasElemType = data.Objects.Any(o => !string.IsNullOrEmpty(o.ElemType));

        var rows = data.Objects.Take(top).Select(o =>
        {
            var row = new List<string> { o.Type };
            if (hasElemType) row.Add(o.ElemType ?? string.Empty);
            row.Add(DumpHelpers.FormatSize(o.Size));
            if (showAddr) row.Add($"0x{o.Addr:X16}");
            return row.ToArray();
        }).ToList();

        var headers = new List<string> { "Type" };
        if (hasElemType) headers.Add("Element Type");
        headers.Add("Size");
        if (showAddr) headers.Add("Address");
        sink.Table(headers.ToArray(), rows,
            $"Top {rows.Count} of {data.Objects.Count:N0} objects \u2265 {DumpHelpers.FormatSize(data.MinSize)}");
    }

    private static void RenderSegmentBreakdown(IRenderSink sink, LargeObjectsData data)
    {
        if (data.Segments.Count == 0) return;

        sink.Section("By Segment");

        int lohCount = data.Segments.Count(s => s.Kind == "LOH");
        long totalBytes = data.Segments.Sum(s => s.Used);
        sink.Text($"The LOH is split across {lohCount} segment{(lohCount == 1 ? "" : "s")} totalling {DumpHelpers.FormatSize(totalBytes)}. " +
                  $"A healthy process typically has 1–2 LOH segments. " +
                  $"Each segment is capped at ~256 MB; when it fills, the GC allocates a new one. " +
                  $"A high segment count means the LOH has grown significantly and is never compacted — " +
                  $"use ArrayPool<T> or MemoryPool<T> to reduce large allocations.");

        if (lohCount >= 10)
            sink.Alert(AlertLevel.Critical,
                $"{lohCount} LOH segments ({DumpHelpers.FormatSize(totalBytes)} total) — the LOH has grown uncontrolled.",
                "LOH is never compacted. Each new segment permanently reserves virtual address space until all objects inside it are collected.",
                "Use ArrayPool<byte>.Shared or MemoryPool<T> for large temporary buffers. Avoid caching large arrays.");
        else if (lohCount >= 4)
            sink.Alert(AlertLevel.Warning,
                $"{lohCount} LOH segments ({DumpHelpers.FormatSize(totalBytes)} total) — LOH is growing.",
                advice: "Monitor for further growth. Consider ArrayPool<T> for large temporary allocations.");

        bool showKind = data.MinSize != 85_000;
        var rows = data.Segments.Select(s =>
        {
            var row = new List<string>();
            if (showKind) row.Add(s.Kind);
            row.Add($"0x{s.Address:X16}");
            row.Add(s.ObjectCount.ToString("N0"));
            row.Add(DumpHelpers.FormatSize(s.Used));
            return row.ToArray();
        }).ToList();
        var headers = new List<string>();
        if (showKind) headers.Add("Kind");
        headers.Add("Address");
        headers.Add("Objects");
        headers.Add("Total Size");
        sink.Table(headers.ToArray(), rows, "By segment");
    }

    private static void RenderLohFreeSpace(IRenderSink sink, LargeObjectsData data)
    {
        if (data.LohCommitted <= 0) return;

        sink.Section("LOH Free Space Analysis");
        double lohFragPct = data.LohCommitted > 0 ? data.LohFree * 100.0 / data.LohCommitted : 0;
        sink.KeyValues([
            ("LOH committed",     DumpHelpers.FormatSize(data.LohCommitted)),
            ("LOH live objects",  DumpHelpers.FormatSize(data.LohLive)),
            ("LOH free (holes)",  DumpHelpers.FormatSize(data.LohFree)),
            ("LOH fragmentation", $"{lohFragPct:F1}%"),
        ]);

        // LOH live vs free stacked bar
        var lohSegs = new List<(string, double)>();
        if (data.LohLive > 0) lohSegs.Add(("Live objects", (double)data.LohLive));
        if (data.LohFree > 0) lohSegs.Add(("Free (holes)", (double)data.LohFree));
        if (lohSegs.Count > 1)
            sink.StackedBar(lohSegs, null, "LOH committed: live vs. free space", valueMode: "size");

        if (lohFragPct >= 50)
            sink.Alert(AlertLevel.Critical,
                $"LOH is {lohFragPct:F0}% fragmented. Reuse of large arrays is being prevented by holes.",
                "LOH is not compacted by default. Fragmented LOH wastes virtual address space.",
                "Use ArrayPool<T>.Shared, MemoryPool<T>, or enable GCSettings.LargeObjectHeapCompactionMode.");
        else if (lohFragPct >= 25)
            sink.Alert(AlertLevel.Warning,
                $"LOH fragmentation at {lohFragPct:F0}%. Monitor for growth.",
                advice: "Consider ArrayPool<byte>.Shared for large temporary buffers.");
    }
}
