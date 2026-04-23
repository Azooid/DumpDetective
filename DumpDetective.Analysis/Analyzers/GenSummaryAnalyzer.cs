using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class GenSummaryAnalyzer
{
    public GenSummaryData Analyze(DumpContext ctx)
    {
        var (gen0, gen1, gen2, loh, poh, frozen, segments) = ScanSegments(ctx);
        var (gen0c, gen1c, gen2c, frozenObj, frozenSize, pohObj, pohSize) = ScanObjectCounts(ctx);

        return new GenSummaryData(
            Gen0Bytes:      gen0,
            Gen1Bytes:      gen1,
            Gen2Bytes:      gen2,
            LohBytes:       loh,
            PohBytes:       poh,
            FrozenBytes:    frozen,
            Gen0ObjCount:   gen0c,
            Gen1ObjCount:   gen1c,
            Gen2ObjCount:   gen2c,
            Segments:       segments,
            FrozenObjCount: frozenObj,
            FrozenObjSize:  frozenSize,
            PohObjCount:    pohObj,
            PohObjSize:     pohSize,
            HeapWalkable:   ctx.Heap.CanWalkHeap);
    }

    private static (long Gen0, long Gen1, long Gen2, long Loh, long Poh, long Frozen,
                    IReadOnlyList<SegmentRow> Segments)
        ScanSegments(DumpContext ctx)
    {
        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, frozen = 0;
        var segments = new List<SegmentRow>();

        foreach (var seg in ctx.Heap.Segments)
        {
            long committed = (long)seg.CommittedMemory.Length;
            string kind;
            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: gen0   += committed; kind = "Gen0";    break;
                case GCSegmentKind.Generation1: gen1   += committed; kind = "Gen1";    break;
                case GCSegmentKind.Generation2: gen2   += committed; kind = "Gen2";    break;
                case GCSegmentKind.Large:       loh    += committed; kind = "LOH";     break;
                case GCSegmentKind.Pinned:      poh    += committed; kind = "POH";     break;
                case GCSegmentKind.Frozen:      frozen += committed; kind = "Frozen";  break;
                case GCSegmentKind.Ephemeral:
                    gen0 += (long)seg.Generation0.Length;
                    gen1 += (long)seg.Generation1.Length;
                    gen2 += (long)seg.Generation2.Length;
                    kind = "Ephemeral";
                    break;
                default: kind = seg.Kind.ToString(); break;
            }
            segments.Add(new SegmentRow($"0x{seg.Address:X16}", kind, committed));
        }
        return (gen0, gen1, gen2, loh, poh, frozen, segments);
    }

    private static (long Gen0c, long Gen1c, long Gen2c,
                    long FrozenObj, long FrozenSize, long PohObj, long PohSize)
        ScanObjectCounts(DumpContext ctx)
    {
        if (!ctx.Heap.CanWalkHeap) return (0, 0, 0, 0, 0, 0, 0);

        // Fast path — reuse shared snapshot built by DumpCollector
        if (ctx.Snapshot is { } snap)
            return (snap.Gen0ObjCount, snap.Gen1ObjCount, snap.Gen2ObjCount,
                    snap.FrozenObjCount, snap.FrozenObjSize,
                    snap.PohObjCount, snap.PohObjSize);

        long gen0c = 0, gen1c = 0, gen2c = 0;
        long frozenObj = 0, frozenSize = 0, pohObj = 0, pohSize = 0;

        CommandBase.RunStatus("Counting objects per generation...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Counting objects per generation \u2014 {count:N0} objects  \u2022  gen0:{gen0c:N0}  gen1:{gen1c:N0}  gen2:{gen2c:N0}  loh:{pohObj:N0}...");
                    sw.Restart();
                }
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
                    case GCSegmentKind.Frozen:
                        frozenObj++;
                        frozenSize += (long)obj.Size;
                        break;
                    case GCSegmentKind.Pinned:
                        pohObj++;
                        pohSize += (long)obj.Size;
                        break;
                }
            }
        });
        return (gen0c, gen1c, gen2c, frozenObj, frozenSize, pohObj, pohSize);
    }
}
