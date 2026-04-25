using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Lists all GC-pinned objects by enumerating GC handles with <c>IsPinned == true</c>.
/// Pinned objects prevent the GC from compacting the heap around them, causing
/// fragmentation over time. Two kinds are reported:
///   - <c>ClrHandleKind.Pinned</c>: explicit <c>GCHandle.Alloc(obj, Pinned)</c> pins.
///   - Other handle kinds with <c>IsPinned</c> set: async-pinned handles created by
///     the runtime for overlapped I/O operations (e.g. socket buffers).
/// Generation label is resolved from the containing segment kind.
/// </summary>
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
