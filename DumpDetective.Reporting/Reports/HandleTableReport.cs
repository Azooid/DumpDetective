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

        long totalSize    = data.ByKind.Values.Sum(k => k.TotalSize);
        int  strongCount  = data.ByKind.TryGetValue("Strong",  out var sc) ? sc.Count : 0;
        int  pinnedCount  = data.ByKind.TryGetValue("Pinned",  out var pc) ? pc.Count : 0;
        long strongSize   = sc?.TotalSize ?? 0;
        long pinnedSize   = pc?.TotalSize ?? 0;

        sink.KeyValues([
            ("Total GC handles",       data.Total.ToString("N0")),
            ("Estimated size (all)",   DumpHelpers.FormatSize(totalSize)),
            ("Strong handles",         $"{strongCount:N0}  ({DumpHelpers.FormatSize(strongSize)})"),
            ("Pinned handles",         $"{pinnedCount:N0}  ({DumpHelpers.FormatSize(pinnedSize)})"),
        ]);

        if (strongSize > 200 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"Strong handles holding {DumpHelpers.FormatSize(strongSize)} — may indicate long-lived pinned or interop references.");

        if (pinnedCount > 200)
            sink.Alert(AlertLevel.Warning, $"{pinnedCount} pinned handles — excessive pinning can cause GC heap fragmentation.");

        RenderSummaryTable(sink, data);
        RenderPerKindBreakdown(sink, data, topN);
    }

    private static void RenderSummaryTable(IRenderSink sink, HandleTableData data)
    {
        long grandSize = data.ByKind.Values.Sum(k => k.TotalSize);
        var rows = data.ByKind
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new[]
            {
                kv.Key,
                kv.Value.Count.ToString("N0"),
                DumpHelpers.FormatSize(kv.Value.TotalSize),
                $"{kv.Value.Count * 100.0 / Math.Max(1, data.Total):F1}%",
            }).ToList();
        sink.Table(["Handle Kind", "Count", "Est. Size", "% of All"], rows,
            $"{data.Total:N0} total handles");
    }

    private static void RenderPerKindBreakdown(IRenderSink sink, HandleTableData data, int topN)
    {
        sink.Section($"Top {topN} Types per Handle Kind");
        foreach (var (kind, info) in data.ByKind.OrderByDescending(kv => kv.Value.Count))
        {
            if (info.Types.Count == 0) continue;
            sink.BeginDetails($"{kind}  ({info.Count:N0} handles  |  {DumpHelpers.FormatSize(info.TotalSize)})", open: false);
            var rows = info.Types
                .OrderByDescending(kv => kv.Value.Size)
                .Take(topN)
                .Select(kv => new[]
                {
                    kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size),
                }).ToList();
            sink.Table(["Object Type", "Count", "Size"], rows);
            sink.EndDetails();
        }
    }
}
