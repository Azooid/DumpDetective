using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Enumerates objects at or above the LOH allocation threshold (default 85 KB).
// Shows a type aggregate view, individual largest objects, segment breakdown,
// and LOH fragmentation analysis.
internal static class LargeObjectsCommand
{
    private const string Help = """
        Usage: DumpDetective large-objects <dump-file> [options]

        Options:
          -n, --top <N>          Show top N objects by size (default: 50)
          -s, --min-size <bytes> Minimum object size (default: 85000)
          -f, --filter <name>    Only show types whose name contains <name>
          -a, --addresses        Show object addresses
          --type-breakdown       Show aggregate size by type instead of individual objects
          -o, --output <file>    Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50;
        long minSize = 85_000;
        string? filter = null;
        bool showAddr = false;
        bool typeBreakdown = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)
                int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-size" or "-s") && i + 1 < args.Length)
                long.TryParse(args[++i], out minSize);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length)
                filter = args[++i];
            else if (args[i] is "--addresses" or "-a")
                showAddr = true;
            else if (args[i] == "--type-breakdown")
                typeBreakdown = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minSize, filter, showAddr, typeBreakdown));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
        int top = 50, long minSize = 85_000, string? filter = null,
        bool showAddr = false, bool typeBreakdown = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Large Objects",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var tuples = ScanLargeObjects(ctx, minSize, filter);
        if (tuples.Count == 0) { sink.Text($"No objects ≥ {DumpHelpers.FormatSize(minSize)} found."); return; }

        sink.KeyValues([
            ("Objects found",  tuples.Count.ToString("N0")),
            ("Total size",     DumpHelpers.FormatSize(tuples.Sum(r => r.Size))),
            ("Largest object", DumpHelpers.FormatSize(tuples.Max(r => r.Size))),
        ]);

        var typeAgg = tuples
            .GroupBy(r => r.Type)
            .Select(g => (Type: g.Key, ElemType: g.First().ElemType, Count: g.Count(), Size: g.Sum(r => r.Size)))
            .OrderByDescending(t => t.Size)
            .ToList();

        RenderTypeAggregate(sink, typeAgg, tuples.Sum(r => r.Size), top, minSize);
        if (!typeBreakdown) RenderIndividualObjects(sink, tuples, top, minSize, showAddr);
        RenderSegmentBreakdown(sink, tuples);
        RenderLohFreeSpace(sink, ctx);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Collects objects at or above minSize from the heap, applying the optional type-name filter.
    static List<(string Type, string ElemType, long Size, string Seg, string Addr)>
        ScanLargeObjects(DumpContext ctx, long minSize, string? filter)
    {
        var tuples = new List<(string Type, string ElemType, long Size, string Seg, string Addr)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Finding objects ≥ {DumpHelpers.FormatSize(minSize)}...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                long size = (long)obj.Size;
                if (size < minSize) continue;
                var name = obj.Type.Name ?? "<unknown>";
                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                string elemType = obj.Type.ComponentType?.Name ?? "";
                tuples.Add((name, elemType, size, DumpHelpers.SegmentKindLabel(ctx.Heap, obj.Address), $"0x{obj.Address:X16}"));
            }
        });
        return tuples;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Type-aggregate table (Count + TotalSize + % of all found objects).
    static void RenderTypeAggregate(
        IRenderSink sink,
        IReadOnlyList<(string Type, string ElemType, int Count, long Size)> typeAgg,
        long grandTotal, int top, long minSize)
    {
        var aggRows = typeAgg.Take(top).Select(t => new[]
        {
            t.Type,
            t.ElemType.Length > 0 ? t.ElemType : "—",
            t.Count.ToString("N0"),
            DumpHelpers.FormatSize(t.Size),
            grandTotal > 0 ? $"{t.Size * 100.0 / grandTotal:F1}%" : "?",
        }).ToList();
        sink.Section("Type Aggregate");
        sink.Table(["Type", "Element Type", "Count", "Total Size", "% of LOH+"], aggRows,
            $"Top {aggRows.Count} types ≥ {DumpHelpers.FormatSize(minSize)}");
    }

    // Individual largest objects table, sorted by size descending.
    static void RenderIndividualObjects(
        IRenderSink sink,
        IReadOnlyList<(string Type, string ElemType, long Size, string Seg, string Addr)> tuples,
        int top, long minSize, bool showAddr)
    {
        var sorted = tuples.OrderByDescending(r => r.Size).Take(top).ToList();
        string[] headers = showAddr
            ? ["Type", "Element Type", "Size", "Segment", "Address"]
            : ["Type", "Element Type", "Size", "Segment"];
        var rows = sorted.Select(r => showAddr
            ? new[] { r.Type, r.ElemType.Length > 0 ? r.ElemType : "—", DumpHelpers.FormatSize(r.Size), r.Seg, r.Addr }
            : new[] { r.Type, r.ElemType.Length > 0 ? r.ElemType : "—", DumpHelpers.FormatSize(r.Size), r.Seg }
        ).ToList();
        sink.Section($"Top {sorted.Count} Largest Individual Objects");
        sink.Table(headers, rows, $"Top {sorted.Count} of {tuples.Count:N0} objects ≥ {DumpHelpers.FormatSize(minSize)}");
    }

    // Segment breakdown: groups found objects by segment kind.
    static void RenderSegmentBreakdown(
        IRenderSink sink,
        IReadOnlyList<(string Type, string ElemType, long Size, string Seg, string Addr)> tuples)
    {
        var bySegment = tuples.GroupBy(r => r.Seg)
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(r => r.Size)) })
            .OrderByDescending(r => r[2])
            .ToList();
        sink.Table(["Segment", "Objects", "Total Size"], bySegment, "By segment");
    }

    // LOH committed/live/free summary + compaction advisory alert.
    static void RenderLohFreeSpace(IRenderSink sink, DumpContext ctx)
    {
        sink.Section("LOH Free Space Analysis");
        long lohCommitted = 0, lohFree = 0, lohLive = 0;
        var  freeType     = ctx.Heap.FreeType;
        foreach (var seg in ctx.Heap.Segments.Where(s => s.Kind == GCSegmentKind.Large))
            lohCommitted += (long)seg.CommittedMemory.Length;

        if (lohCommitted > 0)
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;
                var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                if (seg?.Kind != GCSegmentKind.Large) continue;
                long size = (long)obj.Size;
                if (obj.Type == freeType) lohFree += size;
                else                      lohLive += size;
            }
            double lohFragPct = lohCommitted > 0 ? lohFree * 100.0 / lohCommitted : 0;
            sink.KeyValues([
                ("LOH committed",     DumpHelpers.FormatSize(lohCommitted)),
                ("LOH live objects",  DumpHelpers.FormatSize(lohLive)),
                ("LOH free (holes)",  DumpHelpers.FormatSize(lohFree)),
                ("LOH fragmentation", $"{lohFragPct:F1}%"),
            ]);
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
        else
        {
            sink.Text("No LOH segments found.");
        }
    }
}