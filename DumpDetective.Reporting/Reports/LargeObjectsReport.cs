using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class LargeObjectsReport
{
    public void Render(LargeObjectsData data, IRenderSink sink,
        int top = 50, bool showAddr = false, bool typeBreakdown = false)
    {
        if (data.Objects.Count == 0) { sink.Text("No large objects found."); return; }

        sink.KeyValues([
            ("Objects found",   data.Objects.Count.ToString("N0")),
            ("Total size",      DumpHelpers.FormatSize(data.TotalSize)),
            ("Largest object",  DumpHelpers.FormatSize(data.Objects[0].Size)),
        ]);

        if (data.TotalSize > 500 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"LOH/Large object total: {DumpHelpers.FormatSize(data.TotalSize)}",
                "Large objects are allocated directly in the LOH which is only collected on Gen2 GC.",
                "Pool large buffers using ArrayPool<byte> or MemoryPool<T> to reduce LOH pressure.");

        RenderTypeAggregate(sink, data, top);
        if (!typeBreakdown) RenderIndividualObjects(sink, data, top, showAddr);
        RenderSegmentBreakdown(sink, data);
    }

    private static void RenderTypeAggregate(IRenderSink sink, LargeObjectsData data, int top)
    {
        sink.Section("Type Aggregate");
        var typeAgg = data.Objects
            .GroupBy(o => o.Type)
            .Select(g => (Type: g.Key, Count: g.Count(), Size: g.Sum(o => o.Size)))
            .OrderByDescending(t => t.Size)
            .Take(top)
            .ToList();
        var rows = typeAgg.Select(t => new[]
        {
            t.Type, t.Count.ToString("N0"), DumpHelpers.FormatSize(t.Size),
            $"{t.Size * 100.0 / Math.Max(1, data.TotalSize):F1}%",
        }).ToList();
        sink.Table(["Type", "Count", "Total Size", "% of Total"], rows,
            $"Top {rows.Count} types  |  sorted by total size");
    }

    private static void RenderIndividualObjects(IRenderSink sink, LargeObjectsData data,
        int top, bool showAddr)
    {
        sink.Section($"Top {Math.Min(top, data.Objects.Count)} Individual Objects");
        var rows = data.Objects.Take(top).Select(o =>
        {
            var row = new List<string> { o.Type, DumpHelpers.FormatSize(o.Size), o.Segment };
            if (!string.IsNullOrEmpty(o.ElemType)) row.Insert(1, o.ElemType);
            if (showAddr) row.Add($"0x{o.Addr:X16}");
            return row.ToArray();
        }).ToList();

        var headers = new List<string> { "Type", "Size", "Segment" };
        if (data.Objects.Any(o => !string.IsNullOrEmpty(o.ElemType))) headers.Insert(1, "Element Type");
        if (showAddr) headers.Add("Address");
        sink.Table(headers.ToArray(), rows);
    }

    private static void RenderSegmentBreakdown(IRenderSink sink, LargeObjectsData data)
    {
        if (data.Segments.Count == 0) return;
        sink.Section("Heap Segments");
        var rows = data.Segments.Select(s => new[]
        {
            s.Kind, s.ObjectCount.ToString("N0"),
            DumpHelpers.FormatSize(s.Used),
            DumpHelpers.FormatSize(s.Reserved),
        }).ToList();
        sink.Table(["Kind", "Objects Found", "Used", "Reserved"], rows);
    }
}
