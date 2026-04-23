using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

public sealed class MemoryLeakAnalyzer
{
    public MemoryLeakData Analyze(DumpContext ctx,
        int top = 30, int minCount = 500,
        bool noRootTrace = false, bool includeSystem = false)
    {
        // Step 1: build per-type stats (Count, Size, LOH/Gen2 breakdowns, sample addresses)
        // Fast-path: HeapSnapshot built during DumpCollector.CollectFull already has all this
        // data in TypeAgg — no heap walk needed (~18-26s saved for large dumps).
        // Slow-path: fall back to own heap walk when no snapshot is available.
        var typeStats = new Dictionary<string, (long Count, long Size, long LohSize, long LohCount, long Gen2Count, long Gen2Size, string Gen, ulong SampleAddr, ulong MT)>(
            StringComparer.Ordinal);
        var typeSamples = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);

        if (ctx.Snapshot is { } snapFast)
        {
            // Fast path — read TypeAgg directly; no I/O, effectively instant
            CommandBase.RunStatus("Reading type stats from snapshot cache (Step 1: fast-path)...", () =>
            {
                foreach (var (name, agg) in snapFast.TypeStats)
                {
                    typeStats[name] = (agg.Count, agg.Size, agg.Ls, agg.Lc, agg.G2c, agg.G2s,
                                       agg.GenLabel, agg.SampleAddrs.Count > 0 ? agg.SampleAddrs[0] : 0, agg.MT);
                    if (agg.SampleAddrs.Count > 0)
                        typeSamples[name] = [.. agg.SampleAddrs.Take(3)];
                }
            });
        }
        else
        {
            // Slow path — full heap walk
            CommandBase.RunStatus("Walking heap (Step 1: dumpheap-stat)...", update =>
            {
                long count = 0;
                var  sw    = System.Diagnostics.Stopwatch.StartNew();
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    count++;
                    if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Walking heap \u2014 {count:N0} objects  \u2022  {typeStats.Count:N0} types  \u2022  size:{DumpHelpers.FormatSize(typeStats.Values.Sum(e => e.Size))}...");
                        sw.Restart();
                    }
                    string name = obj.Type.Name ?? "<unknown>";
                    long   size = (long)obj.Size;
                    var    seg  = ctx.Heap.GetSegmentByAddress(obj.Address);
                    bool   isLoh  = seg?.Kind == GCSegmentKind.Large;
                    bool   isPoh  = seg?.Kind == GCSegmentKind.Pinned;
                    bool   isGen2 = seg?.Kind == GCSegmentKind.Generation2 ||
                                    (seg?.Kind == GCSegmentKind.Ephemeral && seg is not null &&
                                     !seg.Generation0.Contains(obj.Address) &&
                                     !seg.Generation1.Contains(obj.Address));

                    ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(typeStats, name, out bool existed);
                    if (existed)
                    {
                        entry = (entry.Count + 1, entry.Size + size,
                                 entry.LohSize   + (isLoh  ? size : 0),
                                 entry.LohCount  + (isLoh  ? 1    : 0),
                                 entry.Gen2Count + (isGen2 ? 1    : 0),
                                 entry.Gen2Size  + (isGen2 ? size : 0),
                                 entry.Gen, entry.SampleAddr, entry.MT);
                        if (typeSamples.TryGetValue(name, out var list) && list.Count < 3)
                            list.Add(obj.Address);
                    }
                    else
                    {
                        string gen = isLoh ? "LOH" : isPoh ? "POH" : "Gen2";
                        entry = (1, size,
                                 isLoh  ? size : 0,
                                 isLoh  ? 1    : 0,
                                 isGen2 ? 1    : 0,
                                 isGen2 ? size : 0,
                                 gen, obj.Address, obj.Type.MethodTable);
                        typeSamples[name] = new List<ulong>(3) { obj.Address };
                    }
                }
            });
        }

        var allTypes = typeStats
            .Select(kv => new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen, kv.Value.MT))
            .OrderByDescending(r => r.Size)
            .ToList();

        long totalHeapSize    = allTypes.Sum(r => r.Size);

        // Get gen totals — from snapshot fast-path or computed from segment walks
        long gen0Total, gen1Total, gen2Total, lohTotal, pohTotal;
        int  totalObjects;
        if (ctx.Snapshot is { } snap2)
        {
            gen0Total    = snap2.Gen0Total;
            gen1Total    = snap2.Gen1Total;
            gen2Total    = snap2.Gen2Total;
            lohTotal     = snap2.LohTotal;
            pohTotal     = snap2.PohTotal;
            totalObjects = (int)snap2.TotalObjects;
        }
        else
        {
            // Derive from per-segment sizes in the typeStats walk
            gen0Total = gen1Total = gen2Total = lohTotal = pohTotal = 0;
            foreach (var seg in ctx.Heap.Segments)
            {
                long len = (long)seg.ObjectRange.Length;
                switch (seg.Kind)
                {
                    case GCSegmentKind.Generation0: gen0Total += len; break;
                    case GCSegmentKind.Generation1: gen1Total += len; break;
                    case GCSegmentKind.Generation2: gen2Total += len; break;
                    case GCSegmentKind.Large:       lohTotal  += len; break;
                    case GCSegmentKind.Pinned:      pohTotal  += len; break;
                }
            }
            totalObjects = allTypes.Sum(r => (int)r.Count);
        }

        // Helper to build a SuspectRow from a typeStats entry
        SuspectRow ToSuspect(KeyValuePair<string, (long Count, long Size, long LohSize, long LohCount, long Gen2Count, long Gen2Size, string Gen, ulong SampleAddr, ulong MT)> kv)
            => new SuspectRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen,
                              kv.Value.Gen2Count, kv.Value.Gen2Size, kv.Value.LohCount, kv.Value.LohSize);

        // Filter for count-based suspects (non-system, high count OR ≥1MB), top 20
        var countSuspects = typeStats
            .Where(kv => (includeSystem || !DumpHelpers.IsSystemType(kv.Key))
                      && (kv.Value.Count >= minCount || kv.Value.Size >= 1_048_576))
            .OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Size)
            .Take(20)
            .Select(ToSuspect)
            .ToList();

        // Size-based suspects: any type ≥10MB NOT already in countSuspects, top 10
        var countSuspectNames = new HashSet<string>(countSuspects.Select(s => s.Name), StringComparer.Ordinal);
        var sizeSuspects = typeStats
            .Where(kv => kv.Value.Size >= 10_485_760 && kv.Value.Count > 0
                      && !countSuspectNames.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value.Size)
            .Take(10)
            .Select(ToSuspect)
            .ToList();

        // Step 3: string stats
        long totalStrSize  = 0;
        long totalStrCount = 0;
        if (typeStats.TryGetValue("System.String", out var strStats))
        {
            totalStrSize  = strStats.Size;
            totalStrCount = strStats.Count;
        }

        // Step 3b: accumulation pattern data — computed from ALL typeStats (not capped)
        var patterns = ComputePatternData(typeStats);

        // Step 4: root chains for suspects (optional) — use up to 3 count + 2 size candidates
        var rootChains = new List<MemoryRootChain>();
        if (!noRootTrace && (countSuspects.Count > 0 || sizeSuspects.Count > 0))
        {
            var countCandidates = countSuspects.Take(3).ToList();
            var countNames      = new HashSet<string>(countCandidates.Select(s => s.Name), StringComparer.Ordinal);
            var sizeCandidates  = sizeSuspects.Where(s => !countNames.Contains(s.Name)).Take(2).ToList();
            var rootCandidates  = countCandidates.Concat(sizeCandidates).ToList();

            // Step 4a: build GC roots map
            var rootMap = new Dictionary<ulong, (string Kind, string? ObjType)>();
            CommandBase.RunStatus("Building GC roots map (Step 4a)...", () =>
            {
                foreach (var root in ctx.Heap.EnumerateRoots())
                {
                    if (root.Object == 0 || rootMap.ContainsKey(root.Object)) continue;
                    string kind = root.RootKind switch
                    {
                        ClrRootKind.Stack             => "Stack (thread local)",
                        ClrRootKind.StrongHandle      => "GC Handle — Strong",
                        ClrRootKind.PinnedHandle      => "GC Handle — Pinned",
                        ClrRootKind.AsyncPinnedHandle => "GC Handle — Async-Pinned",
                        ClrRootKind.RefCountedHandle  => "GC Handle — RefCount",
                        ClrRootKind.FinalizerQueue    => "Finalizer Queue",
                        _                             => root.RootKind.ToString(),
                    };
                    var obj = ctx.Heap.GetObject(root.Object);
                    rootMap[root.Object] = (kind, obj.IsValid ? obj.Type?.Name : null);
                }
            });

            // Step 4b: build full referrer map — heap walk #2 (most expensive step).
            // SharedReferrerCache is shared with HighRefsAnalyzer — whichever of the two
            // parallel workers arrives first builds it; the other gets the result instantly.
            // In full-analyze mode the cache is pre-populated during snapshot collection,
            // so GetOrCreateAnalysis returns instantly (~0 ms).
            SharedReferrerCache? referrerCache = null;
            CommandBase.RunStatus("Building referrer map (heap walk)...", () =>
                referrerCache = ctx.GetOrCreateAnalysis<SharedReferrerCache>(
                    () => SharedReferrerCache.Build(ctx)));
            var allReferrers = referrerCache!.BfsMap;

            // Step 4c: trace root chains for each suspect
            CommandBase.RunStatus($"Tracing root chains (Step 4c — {rootCandidates.Count} suspect types)...", () =>
            {
                foreach (var suspect in rootCandidates)
                {
                    if (!typeStats.TryGetValue(suspect.Name, out var ts)) continue;
                    typeSamples.TryGetValue(suspect.Name, out var addrs);
                    if (addrs is null || addrs.Count == 0) continue;

                    var sampleChains = new List<SampleChain>();
                    foreach (var addr in addrs.Take(3))
                    {
                        var instObj  = ctx.Heap.GetObject(addr);
                        long ownSize = instObj.IsValid ? (long)instObj.Size : 0;
                        var chain    = BuildChainBFS(addr, allReferrers, rootMap, ctx.Heap, maxDepth: 60);
                        sampleChains.Add(new SampleChain(addr, ownSize, chain));
                    }

                    if (sampleChains.Count > 0)
                        rootChains.Add(new MemoryRootChain(suspect.Name, suspect.Count, suspect.Size, sampleChains));
                }
            });

            // Release the referrer cache as soon as BFS tracing is done — the BfsMap is the
            // largest in-memory structure (~1 GB after the cap). Freeing it now lets
            // heap-fragmentation and static-refs run without that pressure.
            referrerCache!.ReleaseIfDone();
        }

        int totalUniqueTypes = allTypes.Count;
        return new MemoryLeakData(allTypes.Take(top * 2).ToList(), countSuspects, sizeSuspects,
            totalHeapSize, gen0Total, gen1Total, gen2Total, lohTotal, pohTotal, totalObjects,
            totalStrSize, totalStrCount, rootChains, patterns, minCount,
            TotalUniqueTypes: totalUniqueTypes);
    }

    private static AccumulationPatternData ComputePatternData(
        Dictionary<string, (long Count, long Size, long LohSize, long LohCount, long Gen2Count, long Gen2Size, string Gen, ulong SampleAddr, ulong MT)> typeStats)
    {
        // Byte arrays
        typeStats.TryGetValue("System.Byte[]", out var byteArrStats);
        long lohSize  = byteArrStats.LohSize;
        long lohCount = byteArrStats.LohCount;

        // Collections
        var topColls = typeStats
            .Where(kv => kv.Key.StartsWith("System.Collections.Generic.List`1",               StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Generic.Dictionary`2",         StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2", StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Generic.HashSet`1",            StringComparison.Ordinal))
            .Select(kv => new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen,
                Gen2Count: kv.Value.Gen2Count, Gen2Size: kv.Value.Gen2Size))
            .OrderByDescending(r => r.Size).Take(8).ToList();
        long collTotal = typeStats
            .Where(kv => kv.Key.StartsWith("System.Collections.Generic.List`1",               StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Generic.Dictionary`2",         StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2", StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Collections.Generic.HashSet`1",            StringComparison.Ordinal))
            .Sum(kv => kv.Value.Size);

        // Delegates
        var topDelegates = typeStats
            .Where(kv => kv.Key.StartsWith("System.EventHandler", StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Action`",      StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Func`",        StringComparison.Ordinal) ||
                         kv.Key == "System.Action" || kv.Key == "System.MulticastDelegate")
            .Select(kv => new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen,
                Gen2Count: kv.Value.Gen2Count, Gen2Size: kv.Value.Gen2Size))
            .OrderByDescending(r => r.Count).Take(6).ToList();
        long delegateCount = topDelegates.Sum(r => r.Count);
        long delegateSize  = topDelegates.Sum(r => r.Size);

        // Tasks / async state machines
        var topTasks = typeStats
            .Where(kv => kv.Key.StartsWith("System.Threading.Tasks.Task",              StringComparison.Ordinal) ||
                         kv.Key.StartsWith("System.Runtime.CompilerServices.AsyncTask", StringComparison.Ordinal) ||
                         (kv.Key.Contains("+<", StringComparison.Ordinal) && kv.Key.Contains(">d__", StringComparison.Ordinal)))
            .Select(kv => new HeapStatRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Value.Gen,
                Gen2Count: kv.Value.Gen2Count, Gen2Size: kv.Value.Gen2Size))
            .OrderByDescending(r => r.Count).Take(8).ToList();
        long taskCount = topTasks.Sum(r => r.Count);
        long taskSize  = topTasks.Sum(r => r.Size);

        typeStats.TryGetValue("System.String", out var strPat);
        return new AccumulationPatternData(
            StringCount:  strPat.Count,
            StringSize:   strPat.Size,
            StringGen2Size: strPat.Gen2Size,
            ByteArrCount: byteArrStats.Count,
            ByteArrSize:  byteArrStats.Size,
            ByteArrLohSize:  lohSize,
            ByteArrLohCount: lohCount,
            CollTotalSize:   collTotal,
            DelegateCount:   delegateCount,
            DelegateSize:    delegateSize,
            TaskCount:       taskCount,
            TaskSize:        taskSize,
            TopCollections:  topColls,
            TopDelegates:    topDelegates,
            TopTasks:        topTasks);
    }

    // BFS from startAddr upward through multi-parent referrer graph until a GC root is reached.
    // Returns chain steps from root down to (but not including) startAddr, matching old BuildChainBFS.
    // Type names are looked up from the heap on demand; at most maxDepth calls per chain = negligible cost.
    private static IReadOnlyList<ChainStep> BuildChainBFS(
        ulong startAddr,
        Dictionary<ulong, ParentSlots> allReferrers,
        Dictionary<ulong, (string Kind, string? ObjType)> rootMap,
        ClrHeap heap,
        int maxDepth)
    {
        try
        {
            // prev[addr] = (came_from, type_of_addr) — "came_from" is address closer to startAddr
            var prev = new Dictionary<ulong, (ulong From, string Type)>();
            prev[startAddr] = (0, "");

            var queue = new Queue<(ulong Addr, int Depth)>();
            queue.Enqueue((startAddr, 0));
            ulong rootAddr = 0;

            while (queue.Count > 0 && rootAddr == 0)
            {
                var (curr, depth) = queue.Dequeue();
                // Is this node a GC root? (skip startAddr itself)
                if (curr != startAddr && rootMap.ContainsKey(curr))
                {
                    rootAddr = curr;
                    break;
                }
                if (depth >= maxDepth) continue;
                if (!allReferrers.TryGetValue(curr, out var ps)) continue;
                for (int pi = 0; pi < ps.Count; pi++)
                {
                    ulong pAddr = ps.Get(pi);
                    if (prev.ContainsKey(pAddr)) continue;
                    string pType = heap.GetObject(pAddr).Type?.Name ?? "?";
                    prev[pAddr] = (curr, pType);
                    queue.Enqueue((pAddr, depth + 1));
                }
            }

            var chain = new List<ChainStep>();

            if (rootAddr == 0)
            {
                chain.Add(new ChainStep(
                    "(no GC root path found — object likely held by a static field, native handle, or circular cluster without direct GC root)",
                    false));
                return chain;
            }

            // Reconstruct path from rootAddr back toward startAddr, then reverse.
            var pathNodes = new List<(ulong Addr, string Display, bool IsRoot)>();
            ulong cur = rootAddr;
            while (cur != 0)
            {
                bool isRoot = rootMap.ContainsKey(cur);
                string display;
                if (isRoot)
                {
                    var (kind, objType) = rootMap[cur];
                    string tp = objType is not null ? $"  [{objType}]" : string.Empty;
                    display = $"{kind}  @0x{cur:X16}{tp}";
                }
                else
                {
                    display = $"{prev[cur].Type}  @0x{cur:X16}";
                }
                pathNodes.Add((cur, display, isRoot));
                if (!prev.TryGetValue(cur, out var p) || p.From == 0) break;
                cur = p.From;
            }

            pathNodes.Reverse();
            foreach (var (addr, display, isRoot) in pathNodes)
            {
                if (addr == startAddr) continue;
                chain.Add(new ChainStep(display, isRoot));
            }
            return chain;
        }
        catch { return []; }
    }
}
