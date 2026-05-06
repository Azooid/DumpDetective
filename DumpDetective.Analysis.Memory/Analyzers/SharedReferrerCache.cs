using DumpDetective.Analysis.Memory.Consumers;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Stores a single parent address per child object for BFS root-chain tracing.
/// One parent is sufficient — BFS climbs one chain to the root and stops.
/// </summary>
internal struct ParentSlots
{
    private ulong _p0;
    internal byte Count;
    internal const byte Max = 1;

    internal void TryAdd(ulong p)
    {
        if (Count == 0) { _p0 = p; Count = 1; }
        // Already have a parent — drop silently; BFS only needs one valid path.
    }

    internal ulong Get(int i) => _p0;
}

/// <summary>
/// Built once per <see cref="DumpContext"/> in a single heap walk and shared between
/// <c>MemoryLeakAnalyzer</c> (BFS root tracing) and <c>HighRefsAnalyzer</c> (referencing type counts).
/// Stored via <see cref="DumpContext.GetOrCreateAnalysis{T}"/> — whichever of the two analyzers
/// runs first builds it; the second receives the already-built result instantly.
/// </summary>
internal sealed class SharedReferrerCache : IDisposable
{
    /// <summary>
    /// Disk-backed child→parent map used by <c>MemoryLeakAnalyzer.BuildChainBFS</c>.
    /// Backed by a sorted binary temp file (~1.28 GB on disk for 80M objects);
    /// the OS pages only the BFS-hot subset (~72 KB) into RAM.
    /// </summary>
    public readonly DiskBackedParentMap ParentMap;

    /// <summary>
    /// For high-refs display: hot address → (referencing type name → count).
    /// Only populated for addresses that were in the top-N inbound-ref list at build time.
    /// </summary>
    public readonly Dictionary<ulong, Dictionary<string, int>> HotAddrTypes;

    private int _releaseCount;

    private SharedReferrerCache(
        DiskBackedParentMap parentMap,
        Dictionary<ulong, Dictionary<string, int>> hotAddrTypes)
    {
        ParentMap    = parentMap;
        HotAddrTypes = hotAddrTypes;
    }

    /// <summary>
    /// Called by each consumer (memory-leak, high-refs) after it has finished reading.
    /// When both have called this, resources are freed and a Gen2 GC is forced.
    /// </summary>
    public void ReleaseIfDone()
    {
        if (Interlocked.Increment(ref _releaseCount) >= 2)
            Release();
    }

    /// <summary>Unconditional release — for callers that know they are the last consumer.</summary>
    public void Release()
    {
        ParentMap.Dispose();         // closes MMF + deletes temp file
        HotAddrTypes.Clear();
        HotAddrTypes.TrimExcess();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public void Dispose() => Release();

    /// <summary>
    /// Builds the cache with a single <c>EnumerateObjects</c> pass.
    /// Hot addresses are derived from the snapshot's inbound-count map using the same
    /// defaults as <c>HighRefsAnalyzer</c> (top=30, minRefs=10).
    /// When <paramref name="progress"/> is provided, emits live spinner updates and a
    /// final <c>[SCAN]Referrer map|count|ms</c> line compatible with <c>ProgressLogger</c>.
    /// </summary>
    internal static SharedReferrerCache Build(DumpContext ctx, Action<string>? progress = null)
    {
        // Hot addresses already pre-distilled in InboundRefConsumer.OnWalkComplete — instant.
        var hotAddrs = new HashSet<ulong>();
        if (ctx.Snapshot is { } snap)
        {
            foreach (var (addr, _) in snap.TopInboundAddrs.Take(30))
                hotAddrs.Add(addr);
        }

        int bfsCapacity = ctx.Snapshot is { } snapSize
            ? (int)Math.Min((long)snapSize.InboundCountsSize, 100_000_000L)
            : 2_000_000;

        // Release InboundCounts (~1.9 GB) — hot addresses are already extracted above.
        ctx.Snapshot?.ReleaseInboundCounts();

        // Parent map is written into the .ddcache folder alongside other caches.
        string parentMapPath = DiskBackedParentMap.CachePath(ctx.DumpPath);

        // Fast path: BFS index already loaded in context → build parent map from CSR
        // without a second heap walk — O(N+E) pure memory, no ClrMD I/O from dump.
        // Use GetOrCreateAnalysis (reads _onceCache) where PreloadAnalysis stored the box;
        // GetAnalysis (reads _analysisCache) would miss it entirely.
        var bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(
            () => new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath))).Cache;
        if (bfsCache is not null)
        {
            // Load hot-addr-types from cache when available — avoids the ~170s CSR edge scan.
            string hotTypesPath = HotAddrTypesCache.CachePath(ctx.DumpPath);
            Dictionary<ulong, Dictionary<string, int>>? hotTypes = null;
            if (HotAddrTypesCache.IsValid(hotTypesPath, ctx.DumpPath))
                hotTypes = HotAddrTypesCache.TryLoad(hotTypesPath);

            // Load parent map from cache when available — avoids the ~120s Array.Sort.
            var existingMap = DiskBackedParentMap.TryLoad(parentMapPath);
            if (existingMap is not null)
            {
                progress?.Invoke("Loading referrer map from cache...");
                hotTypes ??= BuildHotTypesFromBfs(bfsCache, ctx, hotAddrs);
                return new SharedReferrerCache(existingMap, hotTypes);
            }
            return BuildFromBfsCache(bfsCache, ctx, hotAddrs, parentMapPath, hotTypesPath, progress);
        }

        var consumer = new ReferrerConsumer(bfsCapacity, hotAddrs, parentMapPath);
        HeapWalker.Walk(ctx.Heap, [consumer], progress);

        return new SharedReferrerCache(consumer.ParentMap!, consumer.HotTypes);
    }

    /// <summary>
    /// Derives the parent map and HotAddrTypes from an already-loaded
    /// <see cref="BfsIndexCache"/> without performing another heap walk.
    /// <para>
    /// Algorithm:<br/>
    /// 1. One pass over CSR edges to record the first parent index per child node
    ///    using an <c>int[N]</c> array (no dictionary allocations).<br/>
    /// 2. Collect and sort the (childAddr, parentAddr) pairs, then write to
    ///    <see cref="DiskBackedParentMap.WriteFromSortedArrays"/>.<br/>
    /// 3. A second CSR scan resolves HotAddrTypes for the top-N hot addresses
    ///    using lazily-resolved ClrMD type lookups (cached per node index).
    /// </para>
    /// </summary>
    private static SharedReferrerCache BuildFromBfsCache(
        BfsIndexCache bfs, DumpContext ctx, HashSet<ulong> hotAddrs,
        string parentMapPath, string hotTypesPath, Action<string>? progress)
    {
        int n = bfs.NodeCount;
        progress?.Invoke($"Building parent map from BFS index ({n:N0} nodes)...");

        // 1. Record the first parent index per child node (-1 = no parent / root).
        var parentIdxArr = GC.AllocateUninitializedArray<int>(n);
        Array.Fill(parentIdxArr, -1);
        for (int i = 0; i < n; i++)
        {
            int start = bfs.Offsets[i];
            int end   = bfs.Offsets[i + 1];
            for (int e = start; e < end; e++)
            {
                int c = bfs.Children[e];
                if (parentIdxArr[c] < 0)
                    parentIdxArr[c] = i;
            }
        }

        // 2. Collect address pairs for nodes that have a parent.
        int total = 0;
        for (int c = 0; c < n; c++)
            if (parentIdxArr[c] >= 0) total++;

        var childrenArr = GC.AllocateUninitializedArray<ulong>(total);
        var parentsArr  = GC.AllocateUninitializedArray<ulong>(total);
        int pos = 0;
        for (int c = 0; c < n; c++)
        {
            if (parentIdxArr[c] >= 0)
            {
                childrenArr[pos] = bfs.IndexToAddr[c];
                parentsArr[pos]  = bfs.IndexToAddr[parentIdxArr[c]];
                pos++;
            }
        }
        parentIdxArr = null!; // release ~320 MB for 80 M objects
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);

        // 3. Sort by child address — BFS nodes are NOT address-ordered.
        progress?.Invoke($"Sorting {total:N0} child-parent pairs...");
        Array.Sort(childrenArr, parentsArr);

        // 4. Write sorted pairs to disk and open as memory-mapped parent map.
        progress?.Invoke($"Writing parent map ({total:N0} entries) to disk...");
        var parentMap = DiskBackedParentMap.WriteFromSortedArrays(
            childrenArr, parentsArr, total, parentMapPath);
        childrenArr = null!;
        parentsArr  = null!;

        // 5. Build HotAddrTypes via a second CSR scan — still pure memory, no heap walk.
        progress?.Invoke($"Scanning CSR edges for hot-addr-types ({hotAddrs.Count} hot addresses)...");
        var hotTypes = BuildHotTypesFromBfs(bfs, ctx, hotAddrs);
        // Persist so subsequent runs skip this ~170s scan.
        try { HotAddrTypesCache.Save(hotTypesPath, ctx.DumpPath, hotTypes); } catch { }

        return new SharedReferrerCache(parentMap, hotTypes);
    }

    /// <summary>
    /// Scans the forward CSR edges once to find, for each hot address, the types
    /// of all objects that reference it.  Type names are resolved from ClrMD once
    /// per unique parent node and cached in a local dictionary.
    /// </summary>
    private static Dictionary<ulong, Dictionary<string, int>> BuildHotTypesFromBfs(
        BfsIndexCache bfs, DumpContext ctx, HashSet<ulong> hotAddrs)
    {
        var result = new Dictionary<ulong, Dictionary<string, int>>(hotAddrs.Count);
        foreach (var a in hotAddrs)
            result[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);

        if (hotAddrs.Count == 0) return result;

        // Map hot addresses to CSR node indices for O(1) lookup inside the edge scan.
        var hotNodeIdxs = new HashSet<int>(hotAddrs.Count);
        foreach (var a in hotAddrs)
            if (bfs.TryGetIndex(a, out int idx))
                hotNodeIdxs.Add(idx);

        if (hotNodeIdxs.Count == 0) return result;

        // Scan forward edges.  Only nodes that reference a hot node need type resolution.
        // Type names are lazily resolved from ClrMD and cached by node index so each
        // address is looked up at most once (even if it references multiple hot nodes).
        var typeNameCache = new Dictionary<int, string>(4096);

        for (int i = 0; i < bfs.NodeCount; i++)
        {
            int start = bfs.Offsets[i];
            int end   = bfs.Offsets[i + 1];
            for (int e = start; e < end; e++)
            {
                int childIdx = bfs.Children[e];
                if (!hotNodeIdxs.Contains(childIdx)) continue;

                ulong childAddr = bfs.IndexToAddr[childIdx];
                if (!result.TryGetValue(childAddr, out var typeMap)) continue;

                if (!typeNameCache.TryGetValue(i, out string? typeName))
                {
                    typeName = ctx.Heap.GetObject(bfs.IndexToAddr[i]).Type?.Name ?? "<unknown>";
                    typeNameCache[i] = typeName;
                }

                ref int cnt = ref CollectionsMarshal.GetValueRefOrAddDefault(typeMap, typeName, out _);
                cnt++;
            }
        }

        return result;
    }
}
