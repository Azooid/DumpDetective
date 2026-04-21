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
