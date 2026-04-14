using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Reports GC generation sizes from segment metadata and, when the heap is walkable,
// adds per-generation live object counts from a full heap walk.
internal static class GenSummaryCommand
{
    private const string Help = """
        Usage: DumpDetective gen-summary <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — GC Generation Summary",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var (gen0, gen1, gen2, loh, poh, frozen, segRows) = ScanSegments(ctx);
        var (gen0c, gen1c, gen2c) = ScanObjectCounts(ctx);

        long total    = gen0 + gen1 + gen2 + loh + poh + frozen;
        long totalObj = gen0c + gen1c + gen2c;

        RenderGenBreakdown(sink, gen0, gen1, gen2, loh, poh, frozen, total, gen0c, gen1c, gen2c, totalObj);
        sink.Table(["Segment Address", "Kind", "Committed"], segRows, $"{segRows.Count} segment(s)");
        RenderFrozenPohDetail(sink, ctx, frozen, poh);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Reads committed-memory sizes from each ClrMD segment record. Ephemeral segments
    // are sub-divided into Gen0/Gen1/Gen2 by their declared sub-ranges.
    // Returns per-generation sizes (in bytes) and the raw segment-table rows.
    static (long Gen0, long Gen1, long Gen2, long Loh, long Poh, long Frozen, List<string[]> SegRows)
        ScanSegments(DumpContext ctx)
    {
        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, frozen = 0;
        var segRows = new List<string[]>();
        foreach (var seg in ctx.Heap.Segments)
        {
            long committed = (long)seg.CommittedMemory.Length;
            string kind;
            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: gen0   += committed; kind = "Gen0";      break;
                case GCSegmentKind.Generation1: gen1   += committed; kind = "Gen1";      break;
                case GCSegmentKind.Generation2: gen2   += committed; kind = "Gen2";      break;
                case GCSegmentKind.Large:        loh   += committed; kind = "LOH";       break;
                case GCSegmentKind.Pinned:       poh   += committed; kind = "POH";       break;
                case GCSegmentKind.Frozen:       frozen += committed; kind = "Frozen";   break;
                case GCSegmentKind.Ephemeral:
                    gen0 += (long)seg.Generation0.Length;
                    gen1 += (long)seg.Generation1.Length;
                    gen2 += (long)seg.Generation2.Length;
                    kind = "Ephemeral";
                    break;
                default: kind = seg.Kind.ToString(); break;
            }
            segRows.Add([$"0x{seg.Address:X16}", kind, DumpHelpers.FormatSize(committed)]);
        }
        return (gen0, gen1, gen2, loh, poh, frozen, segRows);
    }

    // Walks every live object on the heap (inside a spinner) and increments per-generation
    // counters. Requires CanWalkHeap; returns (0, 0, 0) if the heap is not walkable.
    static (long Gen0c, long Gen1c, long Gen2c) ScanObjectCounts(DumpContext ctx)
    {
        if (!ctx.Heap.CanWalkHeap) return (0, 0, 0);
        long gen0c = 0, gen1c = 0, gen2c = 0;
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Counting objects per generation...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                if (seg is null) continue;
                switch (seg.Kind)
                {
                    case GCSegmentKind.Generation0: gen0c++; break;
                    case GCSegmentKind.Generation1: gen1c++; break;
                    case GCSegmentKind.Generation2: gen2c++; break;
                    case GCSegmentKind.Ephemeral:
                        if      (seg.Generation0.Contains(obj.Address)) gen0c++;
                        else if (seg.Generation1.Contains(obj.Address)) gen1c++;
                        else                                             gen2c++;
                        break;
                }
            }
        });
        return (gen0c, gen1c, gen2c);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Key-value overview + Gen2 dominance alert. Object-count columns are suppressed
    // when the heap was not walkable (totalObj == 0).
    static void RenderGenBreakdown(IRenderSink sink,
        long gen0, long gen1, long gen2, long loh, long poh, long frozen, long total,
        long gen0c, long gen1c, long gen2c, long totalObj)
    {
        sink.Section("Generation Size Breakdown");
        sink.KeyValues([
            ("Gen0",   $"{DumpHelpers.FormatSize(gen0)}{(totalObj > 0 ? $"  ({gen0c:N0} objects)" : "")}"),
            ("Gen1",   $"{DumpHelpers.FormatSize(gen1)}{(totalObj > 0 ? $"  ({gen1c:N0} objects)" : "")}"),
            ("Gen2",   $"{DumpHelpers.FormatSize(gen2)}{(totalObj > 0 ? $"  ({gen2c:N0} objects)" : "")}"),
            ("LOH",    DumpHelpers.FormatSize(loh)),
            ("POH",    DumpHelpers.FormatSize(poh)),
            ("Frozen", DumpHelpers.FormatSize(frozen)),
            ("Total",  DumpHelpers.FormatSize(total)),
        ]);

        if (total > 0 && gen2 > total * 0.70)
            sink.Alert(AlertLevel.Warning,
                $"Gen2 holds {gen2 * 100.0 / total:F0}% of committed heap ({DumpHelpers.FormatSize(gen2)}).",
                advice: "Excessive Gen2 growth indicates long-lived allocations surviving multiple GC cycles. " +
                        "Review object lifetimes — use object pooling for frequently allocated types.");
    }

    // Frozen-segment and POH advisory section. When the heap is walkable, also walks
    // Frozen/Pinned segments to count live objects and measure their total byte footprint.
    static void RenderFrozenPohDetail(IRenderSink sink, DumpContext ctx, long frozen, long poh)
    {
        if (frozen <= 0 && poh <= 0) return;
        sink.Section("Frozen Segment & POH Detail");

        if (frozen > 0)
            sink.Alert(AlertLevel.Info,
                $"Frozen segment: {DumpHelpers.FormatSize(frozen)}.",
                "Frozen segments contain string literals and other read-only data mapped from PE images. " +
                "This memory is shared between processes and cannot be freed until the AppDomain is unloaded.");
        if (poh > 0)
            sink.Alert(AlertLevel.Info,
                $"POH (Pinned Object Heap): {DumpHelpers.FormatSize(poh)}.",
                "The POH segregates pinned objects to reduce fragmentation of Gen0/Gen1/Gen2. " +
                "Objects allocated with MemoryPool<T> / ArrayPool<T> and pinned for I/O land here.",
                "If POH is unexpectedly large, audit pinned buffer sizes and lifetimes.");

        if (!ctx.Heap.CanWalkHeap) return;
        long frozenObj = 0, pohObj = 0, frozenSize = 0, pohSize = 0;
        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
            var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
            if (seg is null) continue;
            if (seg.Kind == GCSegmentKind.Frozen) { frozenObj++; frozenSize += (long)obj.Size; }
            if (seg.Kind == GCSegmentKind.Pinned) { pohObj++;    pohSize    += (long)obj.Size; }
        }
        sink.KeyValues([
            ("Frozen objects", $"{frozenObj:N0}  ({DumpHelpers.FormatSize(frozenSize)})"),
            ("POH objects",    $"{pohObj:N0}  ({DumpHelpers.FormatSize(pohSize)})"),
        ]);
    }
}