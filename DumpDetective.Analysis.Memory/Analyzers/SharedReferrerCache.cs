using DumpDetective.Analysis.Memory.Consumers;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;

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

        // Parent map is written to a temp file next to the dump (same base name, .parent.map).
        // This avoids the 2.56 GB in-memory BfsMap dict entirely.
        string parentMapPath = Path.ChangeExtension(ctx.DumpPath, ".parent.map");

        var consumer = new ReferrerConsumer(bfsCapacity, hotAddrs, parentMapPath);
        HeapWalker.Walk(ctx.Heap, [consumer], progress);

        return new SharedReferrerCache(consumer.ParentMap!, consumer.HotTypes);
    }
}
