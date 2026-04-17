using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Core.Runtime;

/// <summary>
/// Result of a single shared heap walk built once per analysis session and
/// reused across all sub-commands, eliminating redundant <c>EnumerateObjects</c> calls.
/// </summary>
internal sealed class HeapSnapshot
{
    internal Dictionary<string, TypeAgg>             TypeStats     { get; }
    internal Dictionary<ulong, int>                  InboundCounts { get; }
    internal Dictionary<string, (int Count, long TotalSize)> StringGroups { get; }

    // Generation byte totals
    internal long Gen0Total { get; }
    internal long Gen1Total { get; }
    internal long Gen2Total { get; }
    internal long LohTotal  { get; }
    internal long PohTotal  { get; }

    // Generation object counts
    internal long Gen0ObjCount { get; }
    internal long Gen1ObjCount { get; }
    internal long Gen2ObjCount { get; }

    // Frozen / POH detail
    internal long FrozenObjCount { get; }
    internal long FrozenObjSize  { get; }
    internal long PohObjCount    { get; }
    internal long PohObjSize     { get; }

    // Totals
    internal long TotalObjects     { get; }
    internal long TotalRefs        { get; }
    internal long TotalStringCount { get; }
    internal long TotalStringSize  { get; }

    private HeapSnapshot(
        Dictionary<string, TypeAgg> typeStats,
        Dictionary<ulong, int> inboundCounts,
        Dictionary<string, (int, long)> stringGroups,
        long gen0, long gen1, long gen2, long loh, long poh,
        long gen0c, long gen1c, long gen2c,
        long frozenObjCount, long frozenObjSize,
        long pohObjCount, long pohObjSize,
        long totalObjs, long totalRefs,
        long totalStringCount, long totalStringSize)
    {
        TypeStats        = typeStats;
        InboundCounts    = inboundCounts;
        StringGroups     = stringGroups;
        Gen0Total        = gen0; Gen1Total = gen1; Gen2Total = gen2;
        LohTotal         = loh; PohTotal  = poh;
        Gen0ObjCount     = gen0c; Gen1ObjCount = gen1c; Gen2ObjCount = gen2c;
        FrozenObjCount   = frozenObjCount; FrozenObjSize = frozenObjSize;
        PohObjCount      = pohObjCount;    PohObjSize    = pohObjSize;
        TotalObjects     = totalObjs;  TotalRefs        = totalRefs;
        TotalStringCount = totalStringCount; TotalStringSize = totalStringSize;
    }

    /// <summary>Factory used by consumers after a <c>HeapWalker</c> walk.</summary>
    internal static HeapSnapshot Create(
        Dictionary<string, TypeAgg> typeStats,
        Dictionary<ulong, int> inboundCounts,
        Dictionary<string, (int, long)> stringGroups,
        long gen0, long gen1, long gen2, long loh, long poh,
        long gen0c, long gen1c, long gen2c,
        long frozenObjCount, long frozenObjSize,
        long pohObjCount, long pohObjSize,
        long totalObjs, long totalRefs,
        long totalStringCount, long totalStringSize)
        => new(typeStats, inboundCounts, stringGroups,
               gen0, gen1, gen2, loh, poh,
               gen0c, gen1c, gen2c,
               frozenObjCount, frozenObjSize,
               pohObjCount, pohObjSize,
               totalObjs, totalRefs,
               totalStringCount, totalStringSize);

    /// <summary>
    /// Standalone build — walks the heap once when no pre-built snapshot is available.
    /// Called by <see cref="DumpContext.EnsureSnapshot"/>.
    /// </summary>
    internal static HeapSnapshot Build(DumpContext ctx)
    {
        var typeStats     = new Dictionary<string, TypeAgg>(2048, StringComparer.Ordinal);
        var inboundCounts = new Dictionary<ulong, int>(65536);
        var stringGroups  = new Dictionary<string, (int, long)>(StringComparer.Ordinal);

        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0;
        long gen0c = 0, gen1c = 0, gen2c = 0;
        long frozenObjCount = 0, frozenObjSize = 0;
        long pohObjCount = 0, pohObjSize = 0;
        long totalObjs = 0, totalRefs = 0;
        long totalStringCount = 0, totalStringSize = 0;

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;

            string name = obj.Type.Name ?? "<unknown>";
            long   size = (long)obj.Size;
            var    seg  = ctx.Heap.GetSegmentByAddress(obj.Address);

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

            if (!typeStats.TryGetValue(name, out var acc))
            {
                acc = new TypeAgg { Name = name, MT = obj.Type.MethodTable,
                    GenLabel = g0 ? "Gen0" : g1 ? "Gen1" : g2 ? "Gen2" :
                               isL ? "LOH" : isP ? "POH" : isFrozen ? "Frozen" : "Gen2" };
                typeStats[name] = acc;
            }
            acc.Count++; acc.Size += size;
            if (g0)  { acc.G0c++; acc.G0s += size; }
            if (g1)  { acc.G1c++; acc.G1s += size; }
            if (g2)  { acc.G2c++; acc.G2s += size; }
            if (isL) { acc.Lc++;  acc.Ls  += size; }
            if (isP) { acc.Pc++;  acc.Ps  += size; }
            if (acc.SampleAddrs.Count < 5) acc.SampleAddrs.Add(obj.Address);

            if (g0)      { gen0 += size; gen0c++; }
            if (g1)      { gen1 += size; gen1c++; }
            if (g2)      { gen2 += size; gen2c++; }
            if (isL)       loh  += size;
            if (isP)     { poh  += size; pohObjCount++; pohObjSize += size; }
            if (isFrozen){ frozenObjCount++; frozenObjSize += size; }
            totalObjs++;

            try
            {
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(inboundCounts, refAddr, out _);
                    c++;
                    totalRefs++;
                }
            }
            catch { /* skip objects with unreadable reference fields */ }

            if (name == "System.String")
            {
                totalStringCount++;
                totalStringSize += size;
                try
                {
                    var val = obj.AsString(maxLength: 512) ?? string.Empty;
                    ref var sg = ref CollectionsMarshal.GetValueRefOrAddDefault(stringGroups, val, out bool sgExisted);
                    if (sgExisted) sg = (sg.Item1 + 1, sg.Item2 + size);
                    else           sg = (1, size);
                }
                catch { /* skip corrupted string objects */ }
            }
        }

        return new HeapSnapshot(
            typeStats, inboundCounts, stringGroups,
            gen0, gen1, gen2, loh, poh,
            gen0c, gen1c, gen2c,
            frozenObjCount, frozenObjSize,
            pohObjCount, pohObjSize,
            totalObjs, totalRefs,
            totalStringCount, totalStringSize);
    }
}

/// <summary>Per-type statistics accumulated during a heap walk.</summary>
internal sealed class TypeAgg
{
    public string Name     = "";
    public ulong  MT;
    public long   Count, Size;
    public long   G0c, G0s;
    public long   G1c, G1s;
    public long   G2c, G2s;
    public long   Lc,  Ls;
    public long   Pc,  Ps;
    public string GenLabel = "Gen2";
    public readonly List<ulong> SampleAddrs = new(5);
}
