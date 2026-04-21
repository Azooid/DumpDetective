using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates per-type statistics (<see cref="TypeAgg"/> entries) for
/// <see cref="HeapSnapshot.TypeStats"/>.
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
                if      (seg.Generation0.Contains(obj.Address)) g0 = true;
                else if (seg.Generation1.Contains(obj.Address)) g1 = true;
                else                                             g2 = true;
                break;
            default: g2 = true; break;
        }

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
        acc.Count++; acc.Size += size;
        if (g0)  { acc.G0c++; acc.G0s += size; }
        if (g1)  { acc.G1c++; acc.G1s += size; }
        if (g2)  { acc.G2c++; acc.G2s += size; }
        if (isL) { acc.Lc++;  acc.Ls  += size; }
        if (isP) { acc.Pc++;  acc.Ps  += size; }
        if (acc.SampleAddrs.Count < 5) acc.SampleAddrs.Add(obj.Address);

        _totalObjs++;
    }

    public void OnWalkComplete() { }
}
