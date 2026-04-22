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
            return new HighRefsData([], totalObjs, totalRefs, inboundCounts.Count, []);

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

        // Pre-compute ref-count histogram (all objects >= 10 inbound refs)
        (string Label, int Lo, int Hi)[] buckets =
        [
            ("10 – 49",        10,    49),
            ("50 – 99",        50,    99),
            ("100 – 499",     100,   499),
            ("500 – 999",     500,   999),
            ("1 000 – 9 999", 1_000, 9_999),
            ("≥ 10 000",      10_000, int.MaxValue),
        ];
        var histogram = buckets
            .Select(b => (b.Label, Count: inboundCounts.Values.Count(v => v >= b.Lo && v <= b.Hi)))
            .Where(row => row.Count > 0)
            .ToList();

        return new HighRefsData(candidates, totalObjs, totalRefs, inboundCounts.Count, histogram);
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
        // Fast path: reuse the SharedReferrerCache that memory-leak also uses.
        // In full-analyze mode it is pre-populated during snapshot collection (~0 ms).
        // In standalone mode GetOrCreateAnalysis builds it here (one heap walk).
        if (ctx.Snapshot is not null)
        {
            SharedReferrerCache? cache = null;
            CommandBase.RunStatus("Building referrer map (heap walk)...", () =>
                cache = ctx.GetOrCreateAnalysis<SharedReferrerCache>(
                    () => SharedReferrerCache.Build(ctx)));

            var result = new Dictionary<ulong, Dictionary<string, int>>(addrs.Count);
            foreach (var addr in addrs)
            {
                result[addr] = cache!.HotAddrTypes.TryGetValue(addr, out var tm)
                    ? tm
                    : new Dictionary<string, int>(StringComparer.Ordinal);
            }
            // Release the referrer cache after extracting what is needed — frees BfsMap + HotAddrTypes
            // memory as soon as both memory-leak and high-refs have finished consuming it.
            cache!.ReleaseIfDone();
            return result;
        }

        // Slow path: snapshot not available (standalone command without prior CollectFull).
        return BuildReferencingTypesWalk(ctx, addrs);
    }

    private static Dictionary<ulong, Dictionary<string, int>> BuildReferencingTypesWalk(
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
