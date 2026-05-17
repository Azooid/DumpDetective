using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Correlates managed heap memory with thread stack memory to provide a
/// complete picture of managed memory pressure at the time of the dump.
/// Uses GC heap segment enumeration for managed breakdown and
/// ClrThread.StackBase/StackLimit for thread stack totals.
/// </summary>
public sealed class MemoryPressureAnalyzer
{
    public MemoryPressureData Analyze(DumpContext ctx)
    {
        // Build per-segment breakdown from GC heap segments.
        var byKind = new Dictionary<string, MutableRegion>(StringComparer.Ordinal);

        foreach (var seg in ctx.Heap.Segments)
        {
            string kind = DumpHelpers.SegmentKindLabel(seg);
            if (!byKind.TryGetValue(kind, out var region))
                byKind[kind] = region = new MutableRegion(kind);

            long committed = (long)seg.CommittedMemory.Length;
            long reserved  = (long)seg.ReservedMemory.Length;

            // Free objects are tracked by HeapFragmentation; here we estimate live vs free
            // from segment committed vs total length.
            long free = committed > 0
                ? Math.Max(0, committed - (long)seg.ObjectRange.Length) : 0;
            long live = Math.Min(committed, (long)seg.ObjectRange.Length);

            region.Count++;
            region.Committed += committed;
            region.Reserved  += reserved;
            region.Live      += live;
            region.Free      += free;
        }

        var regions = byKind.Values
            .OrderByDescending(r => r.Committed)
            .Select(r => new MemoryRegionSummary(r.Kind, r.Count, r.Committed, r.Reserved, r.Live, r.Free))
            .ToList();

        // Compute generation totals from snapshot if available, else from segments.
        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0;
        if (ctx.Snapshot is { } snap)
        {
            gen0 = snap.Gen0Total;
            gen1 = snap.Gen1Total;
            gen2 = snap.Gen2Total;
            loh  = snap.LohTotal;
            poh  = snap.PohTotal;
        }
        else
        {
            foreach (var seg in ctx.Heap.Segments)
            {
                long len = (long)seg.ObjectRange.Length;
                switch (seg.Kind)
                {
                    case GCSegmentKind.Generation0: gen0 += len; break;
                    case GCSegmentKind.Generation1: gen1 += len; break;
                    case GCSegmentKind.Generation2: gen2 += len; break;
                    case GCSegmentKind.Large:       loh  += len; break;
                    case GCSegmentKind.Pinned:      poh  += len; break;
                    case GCSegmentKind.Ephemeral:
                        // Partition the ephemeral segment across gen0/1/2
                        gen0 += (long)seg.Generation0.Length;
                        gen1 += (long)seg.Generation1.Length;
                        gen2 += Math.Max(0, len - (long)seg.Generation0.Length - (long)seg.Generation1.Length);
                        break;
                }
            }
        }

        // Thread stack totals.
        long stackTotal = 0;
        int threadCount = 0;
        foreach (var t in ctx.Runtime.Threads)
        {
            if (t.IsAlive && t.StackBase > t.StackLimit)
            {
                stackTotal += (long)(t.StackBase - t.StackLimit);
                threadCount++;
            }
        }

        long totalCommitted = regions.Sum(r => r.CommittedBytes);
        long totalReserved  = regions.Sum(r => r.ReservedBytes);
        long totalLive      = regions.Sum(r => r.LiveBytes);
        long totalFree      = regions.Sum(r => r.FreeBytes);

        return new MemoryPressureData(
            ManagedHeapCommitted: totalCommitted,
            ManagedHeapReserved:  totalReserved,
            ManagedHeapLive:      totalLive,
            ManagedHeapFree:      totalFree,
            ThreadStacksCommitted: stackTotal,
            ThreadCount:          threadCount,
            Gen0Bytes:            gen0,
            Gen1Bytes:            gen1,
            Gen2Bytes:            gen2,
            LohBytes:             loh,
            PohBytes:             poh,
            SegmentCount:         ctx.Heap.Segments.Count(),
            RegionSummary:        regions);
    }

    private sealed class MutableRegion(string kind)
    {
        public string Kind     = kind;
        public int    Count;
        public long   Committed;
        public long   Reserved;
        public long   Live;
        public long   Free;
    }
}
