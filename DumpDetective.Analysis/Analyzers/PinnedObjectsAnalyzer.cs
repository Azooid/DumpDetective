using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class PinnedObjectsAnalyzer
{
    public PinnedObjectsData Analyze(DumpContext ctx)
    {
        var items = new List<PinnedItem>();
        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            if (!h.IsPinned || h.Object == 0) continue;
            var obj   = ctx.Heap.GetObject(h.Object);
            string gen = GetGenLabel(ctx, h.Object);
            bool async = h.HandleKind != ClrHandleKind.Pinned;
            items.Add(new PinnedItem(
                obj.Type?.Name ?? "<unknown>",
                h.Object,
                obj.IsValid ? (long)obj.Size : 0L,
                gen,
                async));
        }
        return new PinnedObjectsData(items);
    }

    private static string GetGenLabel(DumpContext ctx, ulong addr)
    {
        var seg = ctx.Heap.GetSegmentByAddress(addr);
        if (seg is null) return "?";
        return seg.Kind switch
        {
            GCSegmentKind.Large    => "LOH",
            GCSegmentKind.Pinned   => "POH",
            GCSegmentKind.Frozen   => "Frozen",
            GCSegmentKind.Ephemeral => EphemeralGen(seg, addr),
            _                      => "Gen2",
        };
    }

    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }
}
