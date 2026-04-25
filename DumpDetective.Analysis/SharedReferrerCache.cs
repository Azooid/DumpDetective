using DumpDetective.Analysis.Consumers;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Stores a single parent address per child object for BFS root-chain tracing.
/// One parent is sufficient — BFS climbs one chain to the root and stops.
/// Struct layout: 1 × ulong (8 B) + bool (1 B) → padded to 16 B by the CLR.
/// Dictionary Entry overhead: 4 (hash) + 4 (next) + 8 (key) + 16 (struct) = 32 B.
/// For 80 M entries: ~2.56 GB vs ~3.84 GB for the 3-parent variant.
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
internal sealed class SharedReferrerCache
{
    /// <summary>
    /// For memory-leak BFS chains: child address → up to <see cref="ParentSlots.Max"/> parent
    /// addresses stored inline as a value-type struct (zero per-entry heap allocations).
    /// Type names are looked up on demand via <c>ClrHeap.GetObject</c> at display time.
    /// </summary>
    public readonly Dictionary<ulong, ParentSlots> BfsMap;

    /// <summary>
    /// For high-refs display: hot address → (referencing type name → count).
    /// Only populated for addresses that were in the top-N inbound-ref list at build time.
    /// Accumulation is unbounded so type distributions are accurate.
    /// </summary>
    public readonly Dictionary<ulong, Dictionary<string, int>> HotAddrTypes;

    /// <summary>
    /// Incremented by each consumer (memory-leak + high-refs) after it finishes reading.
    /// When the count reaches 2, <see cref="ReleaseIfDone"/> clears both maps, freeing memory
    /// before the slower commands (heap-fragmentation, static-refs) reach their peaks.
    /// </summary>
    private int _releaseCount;

    private SharedReferrerCache(
        Dictionary<ulong, ParentSlots> bfsMap,
        Dictionary<ulong, Dictionary<string, int>> hotAddrTypes)
    {
        BfsMap       = bfsMap;
        HotAddrTypes = hotAddrTypes;
    }

    /// <summary>
    /// Called by each consumer (memory-leak, high-refs) after it has finished reading.
    /// When both have called this, BfsMap and HotAddrTypes are cleared and a Gen2 GC
    /// is forced to reclaim backing arrays before static-refs/fragmentation peaks.
    /// </summary>
    public void ReleaseIfDone()
    {
        if (Interlocked.Increment(ref _releaseCount) >= 2)
            Release();
    }

    /// <summary>Unconditional release — for callers that know they are the last consumer.</summary>
    public void Release()
    {
        BfsMap.Clear();
        BfsMap.TrimExcess();
        HotAddrTypes.Clear();
        HotAddrTypes.TrimExcess();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

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

        var consumer = new ReferrerConsumer(bfsCapacity, hotAddrs);
        HeapWalker.Walk(ctx.Heap, [consumer], progress);

        return new SharedReferrerCache(consumer.BfsMap, consumer.HotTypes);
    }
}
