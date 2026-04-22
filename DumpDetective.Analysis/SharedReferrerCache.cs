using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Stores up to 3 parent addresses per child object for BFS root-chain tracing.
/// A value-type struct stored inline in the BfsMap dictionary eliminates per-entry
/// heap allocations in the hot loop (~80 M objects), removing the main source of GC
/// pressure that made the uncapped List&lt;ulong&gt; approach slow.
/// Struct layout: 3 × ulong (24 B) + byte count (1 B) → padded to 32 B by the CLR.
/// Dictionary Entry overhead: 4 (hash) + 4 (next) + 8 (key) + 32 (struct) = 48 B.
/// For 80 M entries: ~3.84 GB vs ~6.4 GB for List&lt;ulong&gt; — and zero small-object allocations.
/// </summary>
internal struct ParentSlots
{
    private ulong _p0, _p1, _p2;
    internal byte Count;
    internal const byte Max = 3;

    internal void TryAdd(ulong p)
    {
        switch (Count)
        {
            case 0: _p0 = p; Count = 1; return;
            case 1: _p1 = p; Count = 2; return;
            case 2: _p2 = p; Count = 3; return;
            // Already have Max parents — drop silently; BFS only needs one valid path.
        }
    }

    internal ulong Get(int i) => i switch { 0 => _p0, 1 => _p1, _ => _p2 };
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
    /// Called by each consumer after it has finished reading. When both consumers have called
    /// this, the large dictionaries are cleared, trimmed, and a non-compacting Gen2 GC is
    /// forced so the ~4–5 GB of backing arrays are physically reclaimed before static-refs
    /// begins its own large BFS allocations.
    /// </summary>
    public void ReleaseIfDone()
    {
        if (System.Threading.Interlocked.Increment(ref _releaseCount) >= 2)
        {
            BfsMap.Clear();
            BfsMap.TrimExcess();
            HotAddrTypes.Clear();
            HotAddrTypes.TrimExcess();
            // Force a blocking, non-compacting Gen2 GC so the freed BfsMap backing arrays
            // are physically reclaimed immediately. Without this the CLR may not run Gen2
            // before static-refs allocates its own large HashSet<ulong> objects, keeping
            // both live simultaneously and reproducing the ~18 GB peak.
            // non-compacting = no LOH movement; pause is typically < 500 ms.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
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
        // Compute hot addresses from the snapshot (instant — already in memory).
        var hotAddrs = new HashSet<ulong>();
        if (ctx.Snapshot is { } snap)
        {
            foreach (var kv in snap.InboundCounts
                .Where(kv => kv.Value >= 10)
                .OrderByDescending(kv => kv.Value)
                .Take(30))
                hotAddrs.Add(kv.Key);
        }

        // Pre-size BfsMap using the exact count of unique referenced objects (InboundCounts.Count).
        // This equals the number of BfsMap entries we will write, eliminating all resize events.
        // Each resize would briefly coexist old+new backing arrays, spiking peak memory.
        // Capped at 100M to guard against pathological heaps; typical entry size is 48 B/entry.
        int bfsInitialCapacity = ctx.Snapshot is { } snapSize
            ? (int)Math.Min((long)snapSize.InboundCounts.Count, 100_000_000L)
            : 2_000_000;
        var bfsMap   = new Dictionary<ulong, ParentSlots>(bfsInitialCapacity);
        var hotTypes = new Dictionary<ulong, Dictionary<string, int>>(hotAddrs.Count);
        foreach (var a in hotAddrs)
            hotTypes[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);

        long processedCount = 0;
        var  totalWatch     = progress is not null ? Stopwatch.StartNew() : null;
        var  rateWatch      = progress is not null ? Stopwatch.StartNew() : null;
        long lastCount      = 0;

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
            ulong  pAddr = obj.Address;

            if (progress is not null)
            {
                processedCount++;
                if ((processedCount & 0x3FF) == 0 && rateWatch!.ElapsedMilliseconds >= 200)
                {
                    double elapsed  = totalWatch!.Elapsed.TotalSeconds;
                    double interval = rateWatch.Elapsed.TotalSeconds;
                    long   delta    = processedCount - lastCount;
                    long   rate     = interval > 0 ? (long)(delta / interval) : 0;
                    lastCount = processedCount;
                    rateWatch.Restart();
                    progress($"Walking heap objects — {processedCount:N0} objs  •  {elapsed:F1}s  •  ~{rate:N0}/s");
                }
            }

            // Fetch type name lazily — only needed for HotAddrTypes, not BfsMap.
            string? pTypeForHot = null;

            try
            {
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0 || refAddr == pAddr) continue;

                    // BFS map — struct stored inline, zero heap allocations in hot path.
                    // CollectionsMarshal.GetValueRefOrAddDefault returns a direct ref to the
                    // struct within the dictionary's Entry array; TryAdd writes a field directly.
                    ref var ps = ref CollectionsMarshal.GetValueRefOrAddDefault(bfsMap, refAddr, out _);
                    ps.TryAdd(pAddr);

                    // Hot-addr type counts — unbounded per hot address.
                    if (hotTypes.TryGetValue(refAddr, out var typeMap))
                    {
                        pTypeForHot ??= obj.Type?.Name ?? "?";
                        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(typeMap, pTypeForHot, out _);
                        c++;
                    }
                }
            }
            catch { }
        }

        if (progress is not null && totalWatch is not null)
            progress($"[SCAN]Referrer map|{processedCount}|{(long)totalWatch.Elapsed.TotalMilliseconds}");

        return new SharedReferrerCache(bfsMap, hotTypes);
    }
}
