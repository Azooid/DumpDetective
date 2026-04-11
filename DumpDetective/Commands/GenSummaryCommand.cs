using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

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

        long gen0  = 0, gen1  = 0, gen2  = 0, loh = 0, poh = 0, frozen = 0;
        long gen0c = 0, gen1c = 0, gen2c = 0;   // object counts
        var  segRows = new List<string[]>();

        // Segment sizes from the segments collection
        foreach (var seg in ctx.Heap.Segments)
        {
            long committed = (long)seg.CommittedMemory.Length;
            string kind;
            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: gen0 += committed; kind = "Gen0";   break;
                case GCSegmentKind.Generation1: gen1 += committed; kind = "Gen1";   break;
                case GCSegmentKind.Generation2: gen2 += committed; kind = "Gen2";   break;
                case GCSegmentKind.Large:        loh  += committed; kind = "LOH";   break;
                case GCSegmentKind.Pinned:       poh  += committed; kind = "POH";   break;
                case GCSegmentKind.Frozen:       frozen += committed; kind = "Frozen"; break;
                case GCSegmentKind.Ephemeral:
                    gen0 += (long)seg.Generation0.Length;
                    gen1 += (long)seg.Generation1.Length;
                    gen2 += (long)seg.Generation2.Length;
                    kind = "Ephemeral"; break;
                default: kind = seg.Kind.ToString(); break;
            }
            segRows.Add([$"0x{seg.Address:X16}", kind, DumpHelpers.FormatSize(committed)]);
        }

        // Object counts per generation — requires a heap walk
        if (ctx.Heap.CanWalkHeap)
        {
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
        }

        long total    = gen0 + gen1 + gen2 + loh + poh + frozen;
        long totalObj = gen0c + gen1c + gen2c;

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

        // Gen2 dominance alert
        if (total > 0 && gen2 > total * 0.70)
            sink.Alert(AlertLevel.Warning,
                $"Gen2 holds {gen2 * 100.0 / total:F0}% of committed heap ({DumpHelpers.FormatSize(gen2)}).",
                advice: "Excessive Gen2 growth indicates long-lived allocations surviving multiple GC cycles. " +
                        "Review object lifetimes — use object pooling for frequently allocated types.");

        sink.Table(["Segment Address", "Kind", "Committed"], segRows, $"{segRows.Count} segment(s)");

        // ── Frozen / POH detail ──────────────────────────────────────────────
        if (frozen > 0 || poh > 0)
        {
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

            // Count objects in Frozen and POH segments
            if (ctx.Heap.CanWalkHeap)
            {
                long frozenObj = 0, pohObj = 0;
                long frozenSize = 0, pohSize = 0;
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                    if (seg is null) continue;
                    if (seg.Kind == GCSegmentKind.Frozen) { frozenObj++; frozenSize += (long)obj.Size; }
                    if (seg.Kind == GCSegmentKind.Pinned) { pohObj++;    pohSize    += (long)obj.Size; }
                }
                sink.KeyValues([
                    ("Frozen objects",     $"{frozenObj:N0}  ({DumpHelpers.FormatSize(frozenSize)})"),
                    ("POH objects",        $"{pohObj:N0}  ({DumpHelpers.FormatSize(pohSize)})"),
                ]);
            }
        }
    }
}