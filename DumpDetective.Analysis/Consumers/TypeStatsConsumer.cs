using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates per-type statistics (<see cref="TypeAgg"/> entries) for
/// <see cref="HeapSnapshot.TypeStats"/>.
/// For every live object the consumer:
///   1. Resolves which GC generation the object lives in by reading the
///      containing segment's <see cref="GCSegmentKind"/> (ephemeral segments
///      are sub-ranged into Gen0/1/2 by address).
///   2. Increments Count, Size and the matching per-gen byte/count fields.
///   3. Keeps up to 5 sample addresses per type for downstream BFS tracing.
/// On merge the per-bucket clone dictionaries are folded into the master by
/// type name; any type first seen in a clone is inserted directly.
/// </summary>
internal sealed class TypeStatsConsumer : IHeapObjectConsumer
{
    public Dictionary<string, TypeAgg> TypeStats { get; } = new(2048, StringComparer.Ordinal);

    private long _totalObjs;
    public long TotalObjects => _totalObjs;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        long size = (long)obj.Size;
        var seg = heap.GetSegmentByAddress(obj.Address);

        // Determine which GC generation this object lives in.
        // Most segments map 1-to-1 with a generation, but Ephemeral segments
        // contain Gen0, Gen1, and Gen2 sub-ranges that must be checked by address.
        bool g0 = false, g1 = false, g2 = false, isL = false, isP = false, isFrozen = false;
        switch (seg?.Kind)
        {
            case GCSegmentKind.Generation0: g0 = true;  break;
            case GCSegmentKind.Generation1: g1 = true;  break;
            case GCSegmentKind.Generation2: g2 = true;  break;
            case GCSegmentKind.Large:       isL = true; break;
            case GCSegmentKind.Pinned:      isP = true; break;
            case GCSegmentKind.Frozen:      isFrozen = true; break;
            case GCSegmentKind.Ephemeral when seg is not null:
                // Sub-range check: Gen0 is newest (highest addresses), Gen2 is oldest.
                if      (seg.Generation0.Contains(obj.Address)) g0 = true;
                else if (seg.Generation1.Contains(obj.Address)) g1 = true;
                else                                             g2 = true;
                break;
            default: g2 = true; break; // unknown segment kind → treat as Gen2
        }

        // Get-or-create the TypeAgg entry. GenLabel is set only on first insert
        // to reflect the generation where most instances were first seen.
        if (!TypeStats.TryGetValue(meta.Name, out var acc))
        {
            acc = new TypeAgg
            {
                Name     = meta.Name,
                MT       = meta.MT,
                GenLabel = g0 ? "Gen0" : g1 ? "Gen1" : g2 ? "Gen2" :
                           isL ? "LOH" : isP ? "POH" : isFrozen ? "Frozen" : "Gen2",
            };
            TypeStats[meta.Name] = acc;
        }

        // Accumulate totals and per-generation breakdown.
        acc.Count++; acc.Size += size;
        if (g0)  { acc.G0c++; acc.G0s += size; }
        if (g1)  { acc.G1c++; acc.G1s += size; }
        if (g2)  { acc.G2c++; acc.G2s += size; }
        if (isL) { acc.Lc++;  acc.Ls  += size; }
        if (isP) { acc.Pc++;  acc.Ps  += size; }

        // Keep up to 5 sample addresses for BFS root-chain tracing downstream.
        if (acc.SampleAddrs.Count < 5) acc.SampleAddrs.Add(obj.Address);

        _totalObjs++;
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new TypeStatsConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (TypeStatsConsumer)other;
        _totalObjs += src._totalObjs;
        foreach (var (name, srcAgg) in src.TypeStats)
        {
            // Type not yet seen in master — insert the clone's entry directly (no copy needed).
            if (!TypeStats.TryGetValue(name, out var dst))
            {
                TypeStats[name] = srcAgg;
                continue;
            }
            // Type exists in both — fold all fields into the master entry.
            // TypeAgg is a class so dst is a reference; mutations update in-place.
            dst.Count += srcAgg.Count; dst.Size += srcAgg.Size;
            dst.G0c += srcAgg.G0c;     dst.G0s += srcAgg.G0s;
            dst.G1c += srcAgg.G1c;     dst.G1s += srcAgg.G1s;
            dst.G2c += srcAgg.G2c;     dst.G2s += srcAgg.G2s;
            dst.Lc  += srcAgg.Lc;      dst.Ls  += srcAgg.Ls;
            dst.Pc  += srcAgg.Pc;      dst.Ps  += srcAgg.Ps;
            // Merge sample addresses up to the 5-address cap.
            foreach (var addr in srcAgg.SampleAddrs)
                if (dst.SampleAddrs.Count < 5) dst.SampleAddrs.Add(addr);
        }
    }
}
