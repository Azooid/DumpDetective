using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Finds the top-N most-referenced objects on the heap ("hot" objects) and
/// shows which types hold the most references to them.
/// Fast path: reads pre-distilled <see cref="HeapSnapshot.TopInboundAddrs"/> and
///   <see cref="HeapSnapshot.InboundHistogram"/> built by <see cref="Consumers.InboundRefConsumer"/>
///   during the main walk — no second heap scan needed for address selection or histogram.
/// Referencing-type breakdown: uses <see cref="SharedReferrerCache"/> (shared with
///   <c>MemoryLeakAnalyzer</c>) — whichever of the two parallel workers arrives first
///   triggers the walk; the other gets the result instantly from the cache.
/// Retained size is estimated by summing the direct children of each hot object
///   (capped at 2 000 children to avoid runaway on array-like objects).
/// </summary>
public sealed class HighRefsAnalyzer
{
    public HighRefsData Analyze(DumpContext ctx, int top = 30, int minRefs = 10)
    {
        HashSet<ulong> topAddrs;
        long totalRefs, totalObjs;
        int  inboundCountsSize;
        Dictionary<ulong, int>? standaloneInboundCounts = null;

        if (ctx.Snapshot is { } snap)
        {
            // Use pre-distilled summary — InboundCounts may already be released.
            totalRefs         = snap.TotalRefs;
            totalObjs         = snap.TotalObjects;
            inboundCountsSize = snap.InboundCountsSize;
            topAddrs          = snap.TopInboundAddrs
                .Where(t => t.Count >= minRefs)
                .Take(top)
                .Select(t => t.Addr)
                .ToHashSet();
        }
        else
        {
            var (counts, refs, objs) = BuildInboundCounts(ctx);
            totalRefs                = refs;
            totalObjs                = objs;
            inboundCountsSize        = counts.Count;
            standaloneInboundCounts  = counts;
            topAddrs                 = counts
                .Where(kv => kv.Value >= minRefs)
                .OrderByDescending(kv => kv.Value)
                .Take(top)
                .Select(kv => kv.Key)
                .ToHashSet();
        }

        if (topAddrs.Count == 0)
            return new HighRefsData([], totalObjs, totalRefs, inboundCountsSize, []);

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

            int inRef = 0;
            if (ctx.Snapshot is { } s)
            {
                foreach (var (a, c) in s.TopInboundAddrs)
                    if (a == addr) { inRef = c; break; }
            }
            else
            {
                standaloneInboundCounts?.TryGetValue(addr, out inRef);
            }

            candidates.Add(new HighRefEntry(typeName, addr, ownSize, retained, gen, inRef, typeMap.Count, topSrc));
        }

        candidates.Sort(static (a, b) => b.InboundRefs.CompareTo(a.InboundRefs));

        // Build histogram from pre-distilled data (fast path) or raw counts (standalone).
        static string BucketLabel(int lo, int hi) => hi == int.MaxValue
            ? $"≥ {lo:N0}"
            : $"{lo:N0} – {hi:N0}";

        List<(string Label, int Count)> histogram;
        if (ctx.Snapshot is { } snapHist)
        {
            histogram = snapHist.InboundHistogram
                .Where(b => b.Count > 0)
                .Select(b => (BucketLabel(b.Lo, b.Hi), b.Count))
                .ToList();
        }
        else if (standaloneInboundCounts is not null)
        {
            (string Label, int Lo, int Hi)[] bucketDefs =
            [
                ("10 – 49",        10,    49),
                ("50 – 99",        50,    99),
                ("100 – 499",     100,   499),
                ("500 – 999",     500,   999),
                ("1 000 – 9 999", 1_000, 9_999),
                ("≥ 10 000",      10_000, int.MaxValue),
            ];
            histogram = bucketDefs
                .Select(b => (b.Label, Count: standaloneInboundCounts.Values.Count(v => v >= b.Lo && v <= b.Hi)))
                .Where(r => r.Count > 0)
                .ToList();
        }
        else
        {
            histogram = [];
        }

        return new HighRefsData(candidates, totalObjs, totalRefs, inboundCountsSize, histogram);
    }

    private static (Dictionary<ulong, int>, long, long) BuildInboundCounts(DumpContext ctx)
    {
        var counts = new Dictionary<ulong, int>(65536);
        long refs = 0, objs = 0;
        CommandBase.RunStatus("Counting inbound references...", update =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                objs++;
                if ((objs & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Counting inbound references \u2014 {objs:N0} objects  \u2022  {refs:N0} refs  \u2022  {counts.Count:N0} tracked...");
                    sw.Restart();
                }
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

        CommandBase.RunStatus("Profiling referencing types...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Profiling referencing types \u2014 {count:N0} objects scanned...");
                    sw.Restart();
                }
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
