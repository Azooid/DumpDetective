using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class LargeObjectsAnalyzer
{
    public LargeObjectsData Analyze(DumpContext ctx, long minSize = 85_000, string? filter = null)
    {
        var objects = new List<LargeObjectEntry>();

        CommandBase.RunStatus($"Finding objects ≥ {DumpHelpers.FormatSize(minSize)}...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                long size = (long)obj.Size;
                if (size < minSize) continue;

                string typeName = obj.Type.Name ?? "<unknown>";
                if (filter is not null && !typeName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                string elemType = obj.Type.IsArray ? (obj.Type.ComponentType?.Name ?? "?") : "";
                string seg      = DetermineSeg(ctx.Heap, obj.Address);

                objects.Add(new LargeObjectEntry(typeName, elemType, size, seg, obj.Address));
            }
        });

        objects.Sort((a, b) => b.Size.CompareTo(a.Size));

        var segments = BuildSegmentBreakdown(ctx.Heap, objects);
        long total   = objects.Sum(o => o.Size);

        return new LargeObjectsData(objects, total, segments);
    }

    private static string DetermineSeg(ClrHeap heap, ulong addr)
    {
        var seg = heap.GetSegmentByAddress(addr);
        if (seg is null) return "Gen2";
        return seg.Kind switch
        {
            GCSegmentKind.Large       => "LOH",
            GCSegmentKind.Pinned      => "POH",
            GCSegmentKind.Frozen      => "Frozen",
            GCSegmentKind.Generation0 => "Gen0",
            GCSegmentKind.Generation1 => "Gen1",
            GCSegmentKind.Generation2 => "Gen2",
            GCSegmentKind.Ephemeral   =>
                seg.Generation0.Contains(addr) ? "Gen0" :
                seg.Generation1.Contains(addr) ? "Gen1" : "Gen2",
            _ => "Gen2",
        };
    }

    private static List<LargeSegmentInfo> BuildSegmentBreakdown(ClrHeap heap, IReadOnlyList<LargeObjectEntry> objects)
    {
        var objsBySeg = objects.GroupBy(o => o.Segment).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<LargeSegmentInfo>();
        foreach (var seg in heap.Segments)
        {
            string kind = seg.Kind switch
            {
                GCSegmentKind.Large       => "LOH",
                GCSegmentKind.Pinned      => "POH",
                GCSegmentKind.Frozen      => "Frozen",
                GCSegmentKind.Generation0 => "Gen0",
                GCSegmentKind.Generation1 => "Gen1",
                _                         => "Gen2",
            };
            int count   = objsBySeg.GetValueOrDefault(kind, 0);
            result.Add(new LargeSegmentInfo(kind, (long)seg.ObjectRange.Length, (long)seg.ReservedMemory.Length, count));
        }
        return result;
    }
}
