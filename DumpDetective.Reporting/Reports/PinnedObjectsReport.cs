using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class PinnedObjectsReport
{
    public void Render(PinnedObjectsData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section("Pinned Objects");
        if (data.Items.Count == 0) { sink.Alert(AlertLevel.Info, "No pinned GC handles found."); return; }

        RenderSummary(data, sink);
        RenderTypeBreakdown(data, sink);
        RenderGenDistribution(data, sink);
        if (showAddr) RenderAddressDetail(data, sink);
    }

    private static void RenderSummary(PinnedObjectsData data, IRenderSink sink)
    {
        int  pinnedCount      = data.Items.Count(i => !i.IsAsyncPinned);
        int  asyncPinnedCount = data.Items.Count(i =>  i.IsAsyncPinned);
        long totalSize        = data.Items.Sum(i => i.Size);
        int  inSohCount       = data.Items.Count(i => i.Gen is "Gen0" or "Gen1" or "Gen2");

        sink.KeyValues([
            ("GCHandle.Pinned",         pinnedCount.ToString("N0")),
            ("Async-Pinned (I/O)",      asyncPinnedCount.ToString("N0")),
            ("Total pinned handles",    data.Items.Count.ToString("N0")),
            ("Total size",              Fmt(totalSize)),
            ("In SOH (Gen0/Gen1/Gen2)", inSohCount.ToString("N0")),
        ]);

        if (data.Items.Count >= 2000)
            sink.Alert(AlertLevel.Critical,
                $"{data.Items.Count:N0} pinned handles — severe fragmentation risk.",
                $"{inSohCount:N0} are in SOH generations which prevents GC compaction.",
                "Replace GCHandle.Alloc(Pinned) with Memory<T>/MemoryPool<T>, or pin only at P/Invoke boundaries.");
        else if (data.Items.Count >= 500)
            sink.Alert(AlertLevel.Warning,
                $"{data.Items.Count:N0} pinned handles — notable fragmentation pressure.",
                $"{inSohCount:N0} in SOH. High pinned counts inflate heap fragmentation.",
                "Audit long-lived pinned arrays and consider fixed() statements scoped to the I/O call.");
        else if (data.Items.Count >= 50)
            sink.Alert(AlertLevel.Info,
                $"{data.Items.Count:N0} pinned handles — monitor for growth.",
                "Acceptable at low counts; watch for steady increase across snapshots.");

        long byteArraySize = data.Items.Where(i => i.TypeName == "System.Byte[]").Sum(i => i.Size);
        if (byteArraySize > 10 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning,
                $"{Fmt(byteArraySize)} in pinned byte[] arrays.",
                "Byte[] is the most common pinned type from socket/file I/O.",
                "Use ArrayPool<byte>.Shared or PipeReader/PipeWriter to avoid pinning.");
    }

    private static void RenderTypeBreakdown(PinnedObjectsData data, IRenderSink sink)
    {
        var rows = data.Items
            .GroupBy(i => i.TypeName)
            .OrderByDescending(g => g.Sum(i => i.Size))
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                Fmt(g.Sum(i => i.Size)),
                g.Count(i =>  i.IsAsyncPinned).ToString("N0"),
                g.Count(i => !i.IsAsyncPinned).ToString("N0"),
            })
            .ToList();
        sink.Table(["Type", "Count", "Total Size", "Async-Pinned", "GC-Pinned"], rows, "Pinned objects by type");
    }

    private static void RenderGenDistribution(PinnedObjectsData data, IRenderSink sink)
    {
        var rows = data.Items
            .GroupBy(i => i.Gen)
            .OrderBy(g => GenSortKey(g.Key))
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), Fmt(g.Sum(i => i.Size)) })
            .ToList();
        sink.Table(["Generation", "Count", "Total Size"], rows,
            "Generation distribution — Gen0/Gen1/Gen2 pinning causes fragmentation");
    }

    private static void RenderAddressDetail(PinnedObjectsData data, IRenderSink sink)
    {
        foreach (var group in data.Items.GroupBy(i => i.TypeName).OrderByDescending(g => g.Count()))
        {
            var rows = group.Take(100).Select(i => new[]
            {
                $"0x{i.Addr:X16}", Fmt(i.Size), i.Gen, i.IsAsyncPinned ? "Async" : "GC",
            }).ToList();
            sink.BeginDetails($"{group.Key}  ({group.Count():N0} handle(s))", open: false);
            sink.Table(["Address", "Size", "Gen", "Kind"], rows);
            sink.EndDetails();
        }
    }

    private static int GenSortKey(string gen) => gen switch
    {
        "Gen0" => 0, "Gen1" => 1, "Gen2" => 2, "LOH" => 3, "POH" => 4, "Frozen" => 5, _ => 6,
    };

    private static string Fmt(long b) => DumpHelpers.FormatSize(b);
}
