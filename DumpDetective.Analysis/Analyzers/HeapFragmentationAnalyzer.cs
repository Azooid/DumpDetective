using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class HeapFragmentationAnalyzer
{
    public HeapFragmentationData Analyze(DumpContext ctx)
    {
        var segments     = ScanSegments(ctx);
        var distribution = ScanFreeDistribution(ctx);
        return new HeapFragmentationData(segments, distribution);
    }

    private static IReadOnlyList<HeapSegmentInfo> ScanSegments(DumpContext ctx)
    {
        // Pass 1: build segment index
        var segData = new Dictionary<ulong, MutableSeg>();
        foreach (var seg in ctx.Heap.Segments)
        {
            segData[seg.Address] = new MutableSeg(
                DumpHelpers.SegmentKindLabel(ctx.Heap, seg.Address),
                seg.Address,
                (long)seg.CommittedMemory.Length);
        }

        // Pass 2: count pinned handles per segment
        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            if (h.HandleKind != ClrHandleKind.Pinned || h.Object == 0) continue;
            var seg = ctx.Heap.GetSegmentByAddress(h.Object);
            if (seg is not null && segData.TryGetValue(seg.Address, out var info))
                info.PinnedCount++;
        }

        // Pass 3: walk heap to split live vs. free per segment
        var freeType = ctx.Heap.FreeType;
        CommandBase.RunStatus("Measuring fragmentation...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;
                var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                if (seg is null || !segData.ContainsKey(seg.Address)) continue;
                var info = segData[seg.Address];
                long size = (long)obj.Size;
                if (obj.Type == freeType) info.FreeBytes += size;
                else                      info.LiveBytes += size;
            }
        });

        return segData.Values
            .Where(s => s.CommittedBytes > 0)
            .OrderByDescending(s => s.CommittedBytes > 0 ? s.FreeBytes * 100.0 / s.CommittedBytes : 0)
            .Select(s => new HeapSegmentInfo(s.Kind, s.Address, s.CommittedBytes, s.LiveBytes, s.FreeBytes, s.PinnedCount))
            .ToList();
    }

    private static IReadOnlyList<FreeHoleBucket> ScanFreeDistribution(DumpContext ctx)
    {
        var freeType = ctx.Heap.FreeType;
        var buckets  = new Dictionary<int, (long Count, long Size)>();

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type != freeType) continue;
            long sz = (long)obj.Size;
            int key = sz switch
            {
                < 128     => 0,
                < 1024    => 1,
                < 4096    => 2,
                < 65536   => 3,
                < 1048576 => 4,
                _         => 5,
            };
            if (!buckets.TryGetValue(key, out var bv)) bv = (0, 0);
            buckets[key] = (bv.Count + 1, bv.Size + sz);
        }

        return buckets
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                string label = kv.Key switch
                {
                    0 => "< 128 B",
                    1 => "128 B – 1 KB",
                    2 => "1 KB – 4 KB",
                    3 => "4 KB – 64 KB",
                    4 => "64 KB – 1 MB",
                    _ => "≥ 1 MB",
                };
                return new FreeHoleBucket(label, kv.Value.Count, kv.Value.Size, kv.Key);
            })
            .ToList();
    }

    private sealed class MutableSeg(string kind, ulong address, long committed)
    {
        public string Kind           = kind;
        public ulong  Address        = address;
        public long   CommittedBytes = committed;
        public long   LiveBytes;
        public long   FreeBytes;
        public int    PinnedCount;
    }
}
