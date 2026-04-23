using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HandleTableReport
{
    public void Render(HandleTableData data, IRenderSink sink, int topN = 5)
    {
        sink.Section("Handle Summary");
        if (data.Total == 0) { sink.Text("No GC handles found."); return; }

        sink.Explain(
            what: "The GC Handle Table tracks managed objects that must remain alive or fixed in memory — typically for interop with native code or async I/O.",
            why: "Handles are categorized by type: Strong (prevents GC), Pinned (fixes in memory for native access), Weak (allows GC), AsyncPinned (OS I/O).",
            impact: "Unreleased Strong handles leak entire object graphs. Excessive Pinned handles fragment the heap. Many handles can indicate resource leaks.",
            bullets: ["Strong handles keep objects alive indefinitely — verify each has a corresponding Free() call", "Pinned handles in SOH generations block compaction and cause fragmentation", "WeakShort handles: collected at Gen0; WeakLong: collected after finalization"],
            action: "Ensure GCHandle.Alloc() is always paired with GCHandle.Free() in a finally block or IDisposable.Dispose()."
        );
        RenderSummaryTable(sink, data);
        RenderPerKindBreakdown(sink, data, topN);
    }

    private static void RenderSummaryTable(IRenderSink sink, HandleTableData data)
    {
        var rows = data.ByKind
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new[]
            {
                kv.Key,
                kv.Value.Count.ToString("N0"),
                DumpHelpers.FormatSize(kv.Value.TotalSize),
            }).ToList();
        sink.Table(["Handle Kind", "Count", "Referenced Size"], rows,
            $"{data.Total:N0} total handles");
        sink.KeyValues([("Total handles", data.Total.ToString("N0"))]);

        if (data.ByKind.TryGetValue("Strong", out var strongInfo) && strongInfo.TotalSize > 500 * 1024 * 1024L)
            sink.Alert(AlertLevel.Critical,
                $"Strong handles reference {DumpHelpers.FormatSize(strongInfo.TotalSize)} of live objects.",
                advice: "Review GCHandle.Alloc(obj, GCHandleType.Normal) usage — these prevent GC of the entire retained graph.");
    }

    private static void RenderPerKindBreakdown(IRenderSink sink, HandleTableData data, int topN)
    {
        sink.Section("Per-Kind Type Breakdown");
        foreach (var (kind, info) in data.ByKind.OrderByDescending(kv => kv.Value.Count))
        {
            if (info.Types.Count == 0) continue;
            sink.BeginDetails(
                $"{kind}  —  {info.Count:N0} handle(s)  |  {DumpHelpers.FormatSize(info.TotalSize)}",
                open: kind is "Strong" or "Pinned");
            var rows = info.Types
                .OrderByDescending(kv => kv.Value.Count)
                .Take(topN)
                .Select(kv => new[]
                {
                    kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size),
                }).ToList();
            if (rows.Count > 0)
                sink.Table(["Object Type", "Count", "Size"], rows,
                    $"Top {rows.Count} types under {kind} handles");
            else
                sink.Text("  (no object type info available)");
            sink.EndDetails();
        }
    }
}
