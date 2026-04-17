using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class GenSummaryReport
{
    public void Render(GenSummaryData data, IRenderSink sink)
    {
        long total    = data.Gen0Bytes + data.Gen1Bytes + data.Gen2Bytes
                      + data.LohBytes + data.PohBytes + data.FrozenBytes;
        long totalObj = data.Gen0ObjCount + data.Gen1ObjCount + data.Gen2ObjCount;

        RenderGenBreakdown(data, sink, total, totalObj);
        RenderSegmentDetails(data, sink);
        RenderFrozenPohDetail(data, sink);
    }

    private static void RenderGenBreakdown(GenSummaryData data, IRenderSink sink, long total, long totalObj)
    {
        sink.Section("Generation Size Breakdown");
        sink.KeyValues([
            ("Gen0",   $"{Fmt(data.Gen0Bytes)}{(totalObj > 0 ? $"  ({data.Gen0ObjCount:N0} objects)" : "")}"),
            ("Gen1",   $"{Fmt(data.Gen1Bytes)}{(totalObj > 0 ? $"  ({data.Gen1ObjCount:N0} objects)" : "")}"),
            ("Gen2",   $"{Fmt(data.Gen2Bytes)}{(totalObj > 0 ? $"  ({data.Gen2ObjCount:N0} objects)" : "")}"),
            ("LOH",    Fmt(data.LohBytes)),
            ("POH",    Fmt(data.PohBytes)),
            ("Frozen", Fmt(data.FrozenBytes)),
            ("Total",  Fmt(total)),
        ]);

        if (total > 0 && data.Gen2Bytes > total * 0.70)
            sink.Alert(AlertLevel.Warning,
                $"Gen2 holds {data.Gen2Bytes * 100.0 / total:F0}% of committed heap ({Fmt(data.Gen2Bytes)}).",
                advice: "Excessive Gen2 growth indicates long-lived allocations surviving multiple GC cycles. " +
                        "Review object lifetimes — use object pooling for frequently allocated types.");
    }

    private static void RenderSegmentDetails(GenSummaryData data, IRenderSink sink)
    {
        var rows = data.Segments
            .Select(s => new[] { s.Address, s.Kind, Fmt(s.CommittedBytes) })
            .ToList();

        sink.BeginDetails($"Segment Details ({rows.Count} segment(s))");
        sink.Table(["Segment Address", "Kind", "Committed"], rows);
        sink.EndDetails();
    }

    private static void RenderFrozenPohDetail(GenSummaryData data, IRenderSink sink)
    {
        if (data.FrozenBytes <= 0 && data.PohBytes <= 0) return;
        sink.Section("Frozen Segment & POH Detail");

        if (data.FrozenBytes > 0)
            sink.Alert(AlertLevel.Info,
                $"Frozen segment: {Fmt(data.FrozenBytes)}.",
                "Frozen segments contain string literals and other read-only data mapped from PE images. " +
                "This memory is shared between processes and cannot be freed until the AppDomain is unloaded.");
        if (data.PohBytes > 0)
            sink.Alert(AlertLevel.Info,
                $"POH (Pinned Object Heap): {Fmt(data.PohBytes)}.",
                "The POH segregates pinned objects to reduce fragmentation of Gen0/Gen1/Gen2. " +
                "Objects allocated with MemoryPool<T> / ArrayPool<T> and pinned for I/O land here.",
                "If POH is unexpectedly large, audit pinned buffer sizes and lifetimes.");

        if (!data.HeapWalkable) return;
        sink.KeyValues([
            ("Frozen objects", $"{data.FrozenObjCount:N0}  ({Fmt(data.FrozenObjSize)})"),
            ("POH objects",    $"{data.PohObjCount:N0}  ({Fmt(data.PohObjSize)})"),
        ]);
    }

    private static string Fmt(long b) => DumpHelpers.FormatSize(b);
}
