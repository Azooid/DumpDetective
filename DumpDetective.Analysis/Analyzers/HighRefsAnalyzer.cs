using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

public sealed class HighRefsAnalyzer
{
    public HighRefsData Analyze(DumpContext ctx, int top = 30, int minRefs = 10)
    {
        Dictionary<ulong, int> inboundCounts;
        long totalRefs, totalObjs;

        // Fast path — reuse HeapSnapshot inbound counts
        if (ctx.Snapshot is { } snap)
        {
            inboundCounts = snap.InboundCounts;
            totalRefs     = snap.TotalRefs;
            totalObjs     = snap.TotalObjects;
        }
        else
        {
            (inboundCounts, totalRefs, totalObjs) = BuildInboundCounts(ctx);
        }

        var topAddrs = inboundCounts
            .Where(kv => kv.Value >= minRefs)
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (topAddrs.Count == 0)
            return new HighRefsData([], totalObjs, totalRefs, inboundCounts.Count);

        var refTypes = BuildReferencingTypes(ctx, topAddrs);
        var candidates = new List<HighRefEntry>(topAddrs.Count);

        foreach (var addr in topAddrs)
        {
            var obj = ctx.Heap.GetObject(addr);
            if (!obj.IsValid) continue;

            string typeName = obj.Type?.Name ?? "<unknown>";
            long ownSize    = (long)obj.Size;
            long retained   = ComputeRetained(ctx.Heap, obj);
            var seg         = ctx.Heap.GetSegmentByAddress(addr);
            string gen = seg?.Kind switch
            {
                GCSegmentKind.Generation0 => "Gen0", GCSegmentKind.Generation1 => "Gen1",
                GCSegmentKind.Generation2 => "Gen2", GCSegmentKind.Large       => "LOH",
                GCSegmentKind.Pinned      => "POH",  GCSegmentKind.Frozen      => "Frozen",
                GCSegmentKind.Ephemeral   => EphemeralGen(seg!, addr), _        => "?",
            };

            var typeMap = refTypes.TryGetValue(addr, out var tm) ? tm : new Dictionary<string, int>();
            var topSrc  = typeMap.OrderByDescending(kv => kv.Value).Take(5)
                              .Select(kv => (kv.Key, kv.Value)).ToList();
            int inRef = inboundCounts.TryGetValue(addr, out int r) ? r : 0;

            candidates.Add(new HighRefEntry(typeName, addr, ownSize, retained, gen, inRef, typeMap.Count, topSrc));
        }

        candidates.Sort(static (a, b) => b.InboundRefs.CompareTo(a.InboundRefs));
        return new HighRefsData(candidates, totalObjs, totalRefs, inboundCounts.Count);
    }

    private static (Dictionary<ulong, int>, long, long) BuildInboundCounts(DumpContext ctx)
    {
        var counts = new Dictionary<ulong, int>(65536);
        long refs = 0, objs = 0;
        CommandBase.RunStatus("Counting inbound references...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                objs++;
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, refAddr, out _);
                    c++;
                    refs++;
                }
            }
        });
        return (counts, refs, objs);
    }

    private static Dictionary<ulong, Dictionary<string, int>> BuildReferencingTypes(
        DumpContext ctx, HashSet<ulong> addrs)
    {
        var result = new Dictionary<ulong, Dictionary<string, int>>(addrs.Count);
        foreach (var a in addrs) result[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);

        CommandBase.RunStatus("Profiling referencing types...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                string srcType = obj.Type.Name ?? "<unknown>";
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0 || !result.TryGetValue(refAddr, out var map)) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(map, srcType, out _);
                    c++;
                }
            }
        });
        return result;
    }

    private static long ComputeRetained(ClrHeap heap, ClrObject obj)
    {
        long total = (long)obj.Size;
        try
        {
            int cap = 0;
            foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (refAddr == 0 || refAddr == obj.Address) continue;
                var child = heap.GetObject(refAddr);
                if (child.IsValid) total += (long)child.Size;
                if (++cap >= 2000) break;
            }
        }
        catch { }
        return total;
    }

    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }
}
