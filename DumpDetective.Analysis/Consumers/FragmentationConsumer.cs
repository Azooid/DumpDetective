using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates per-segment live/free byte totals and the free-hole size distribution
/// for <see cref="DumpDetective.Analysis.Analyzers.HeapFragmentationAnalyzer"/>.
/// One parallel pass via <see cref="HeapWalker"/> replaces the old serial per-segment foreach.
/// For each object:
///   - Free objects (type == freeType): added to the segment's free bytes and bucketed
///     by hole size into logarithmic buckets (power-of-2 boundaries) for the histogram.
///   - Live objects: added to the segment's live bytes.
/// The segment lookup is done by <c>ClrHeap.GetSegmentByAddress</c> and cached in
/// <see cref="SegData"/> keyed by segment base address.
/// Free-hole buckets use the floor log2 of the size so each bucket spans
/// [2^k, 2^(k+1)) bytes — displayed as “&lt; 1 KB”, “1–4 KB”, etc. in the report.
/// </summary>
internal sealed class FragmentationConsumer : IHeapObjectConsumer
{
    // segment address → mutable state; keyed the same way as HeapFragmentationAnalyzer
    public readonly Dictionary<ulong, MutableSeg> SegData;

    // bucket key → (count, totalBytes)
    public readonly Dictionary<int, (long Count, long Size)> Buckets = new();

    private readonly ClrType _freeType;

    public FragmentationConsumer(ClrHeap heap, IEnumerable<ClrSegment> segments, ClrType freeType)
    {
        _freeType = freeType;
        SegData   = new Dictionary<ulong, MutableSeg>();
        foreach (var seg in segments)
            SegData[seg.Address] = new MutableSeg(
                DumpHelpers.SegmentKindLabel(heap, seg.Address),
                seg.Address,
                (long)seg.CommittedMemory.Length);
    }

    // Clone ctor — empty accumulators, same structure
    private FragmentationConsumer(ClrType freeType, Dictionary<ulong, MutableSeg> segTemplate)
    {
        _freeType = freeType;
        SegData   = new Dictionary<ulong, MutableSeg>(segTemplate.Count);
        foreach (var (addr, s) in segTemplate)
            SegData[addr] = new MutableSeg(s.Kind, addr, s.CommittedBytes);
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        // Resolve the segment this object belongs to; skip if not in our pre-built map.
        var seg = heap.GetSegmentByAddress(obj.Address);
        if (seg is null || !SegData.TryGetValue(seg.Address, out var info)) return;

        long size = (long)obj.Size;
        if (obj.Type == _freeType)
        {
            // Free object (GC hole) — add to free bytes and bucket by hole size.
            // Bucket keys are fixed integers 0–5 representing logarithmic size ranges:
            // 0: < 128 B, 1: < 1 KB, 2: < 4 KB, 3: < 64 KB, 4: < 1 MB, 5: >= 1 MB.
            info.FreeBytes += size;
            int key = size switch
            {
                < 128       => 0,
                < 1_024     => 1,
                < 4_096     => 2,
                < 65_536    => 3,
                < 1_048_576 => 4,
                _           => 5,
            };
            // Tuple value type — must use ref to update in-place without a copy.
            ref var bv = ref CollectionsMarshal.GetValueRefOrAddDefault(Buckets, key, out _);
            bv = (bv.Count + 1, bv.Size + size);
        }
        else
        {
            // Live object — add to segment live bytes.
            info.LiveBytes += size;
        }
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new FragmentationConsumer(_freeType, SegData);

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (FragmentationConsumer)other;

        foreach (var (addr, s) in src.SegData)
        {
            if (!SegData.TryGetValue(addr, out var dst)) continue;
            dst.LiveBytes += s.LiveBytes;
            dst.FreeBytes += s.FreeBytes;
        }

        foreach (var (key, sv) in src.Buckets)
        {
            ref var dv = ref CollectionsMarshal.GetValueRefOrAddDefault(Buckets, key, out _);
            dv = (dv.Count + sv.Count, dv.Size + sv.Size);
        }
    }

    public sealed class MutableSeg(string kind, ulong address, long committed)
    {
        public string Kind           = kind;
        public ulong  Address        = address;
        public long   CommittedBytes = committed;
        public long   LiveBytes;
        public long   FreeBytes;
        public int    PinnedCount;
    }
}
