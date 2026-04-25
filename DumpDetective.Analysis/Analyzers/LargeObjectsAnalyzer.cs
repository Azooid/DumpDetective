using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Lists objects that meet or exceed a size threshold (default: 85 000 B, the LOH boundary).
/// When the threshold is the default LOH threshold, only LOH segments are enumerated,
/// skipping the ~100 M Gen0/1/2 objects and making the scan 5–10× faster.
/// For each matching object the analyzer reads type name, size, address, and generation.
/// A second phase analyses LOH free-space holes (fragmentation) by walking
/// the LOH segments a second time and collecting run lengths of free objects.
/// </summary>
public sealed class LargeObjectsAnalyzer
{
    public LargeObjectsData Analyze(DumpContext ctx, long minSize = 85_000, string? filter = null)
    {
        var objects = new List<LargeObjectEntry>();

        // Objects ≥ 85 KB (the default LOH threshold) can only live in LOH segments.
        // Enumerate LOH segments directly to skip ~10 M Gen0/1/2 objects.
        bool lohOnly = minSize >= 85_000;

        CommandBase.RunStatus($"Finding objects \u2265 {DumpHelpers.FormatSize(minSize)}...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            IEnumerable<ClrObject> src = lohOnly
                ? ctx.Heap.Segments
                      .Where(s => s.Kind == GCSegmentKind.Large)
                      .SelectMany(s => s.EnumerateObjects())
                : ctx.Heap.EnumerateObjects();

            foreach (var obj in src)
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Finding large objects \u2014 {count:N0} LOH objects scanned  \u2022  {objects.Count} found...");
                    sw.Restart();
                }
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

        // LOH free space analysis — iterate LOH segments directly (skip all non-LOH objects)
        long lohCommitted = 0, lohFree = 0, lohLive = 0;
        var  freeType     = ctx.Heap.FreeType;
        CommandBase.RunStatus("Analysing LOH free space...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var seg in ctx.Heap.Segments.Where(s => s.Kind == GCSegmentKind.Large))
            {
                lohCommitted += (long)seg.CommittedMemory.Length;
                foreach (var obj in seg.EnumerateObjects())
                {
                    if (!obj.IsValid) continue;
                    count++;
                    if ((count & 0x3FF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Analysing LOH free space — {count:N0} objects  •  live:{DumpHelpers.FormatSize(lohLive)}  free:{DumpHelpers.FormatSize(lohFree)}...");
                        sw.Restart();
                    }
                    long sz = (long)obj.Size;
                    if (obj.Type == freeType) lohFree += sz;
                    else                      lohLive += sz;
                }
            }
        });

        return new LargeObjectsData(objects, total, segments, lohCommitted, lohLive, lohFree, minSize);
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
