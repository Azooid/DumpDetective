using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Builds the BfsMap (child to parent slots) and HotAddrTypes
/// (hot address to referencing type counts) for SharedReferrerCache.
/// Uses 256-stripe locking on BfsMap — 1x memory instead of 8x clones
/// (~10 GB -> ~1.3 GB peak during the referrer walk).
/// </summary>
internal sealed class ReferrerConsumer : IHeapObjectConsumer
{
    public bool IsThreadSafe => true;

    private const int StripeCount = 256; // power of 2

    private readonly object[]                         _locks;
    private readonly Dictionary<ulong, ParentSlots>[] _stripes;

    // HotTypes: only ~30 entries, each locked individually.
    private readonly Dictionary<ulong, Dictionary<string, int>> _hotTypes;
    private readonly object _hotTypesLock = new();

    // Exposed after OnWalkComplete as plain Dictionaries.
    public Dictionary<ulong, ParentSlots>             BfsMap   { get; private set; } = [];
    public Dictionary<ulong, Dictionary<string, int>> HotTypes { get; private set; } = [];

    public ReferrerConsumer(int bfsCapacity, HashSet<ulong> hotAddrs)
    {
        int stripeCapacity = Math.Max(64, bfsCapacity / StripeCount);
        _locks   = new object[StripeCount];
        _stripes = new Dictionary<ulong, ParentSlots>[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            _locks[i]   = new object();
            _stripes[i] = new Dictionary<ulong, ParentSlots>(stripeCapacity);
        }

        _hotTypes = new Dictionary<ulong, Dictionary<string, int>>(hotAddrs.Count);
        foreach (var a in hotAddrs)
            _hotTypes[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        ulong   pAddr      = obj.Address;
        string? pTypeCache = null;

        try
        {
            foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                // Skip null refs and self-references (objects that reference themselves).
                if (refAddr == 0 || refAddr == pAddr) continue;

                // Record pAddr as a parent of refAddr in the BFS map.
                // Same stripe-lock pattern as InboundRefConsumer — low 8 bits of the
                // child address select the shard; 256 shards keep contention < 3%.
                int stripe = (int)(refAddr & (StripeCount - 1));
                lock (_locks[stripe])
                {
                    ref var ps = ref CollectionsMarshal.GetValueRefOrAddDefault(_stripes[stripe], refAddr, out _);
                    ps.TryAdd(pAddr); // stores only 1 parent — enough for one BFS chain
                }

                // For hot addresses (top-30 by inbound refs), also record which type
                // is doing the referencing. pTypeCache is lazily resolved once per object
                // to avoid repeated meta.Name access on the hot path.
                if (_hotTypes.TryGetValue(refAddr, out var typeMap))
                {
                    pTypeCache ??= meta.Name;
                    lock (typeMap) // per-slot lock — only ~30 hot addrs, negligible overhead
                    {
                        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(typeMap, pTypeCache, out _);
                        c++;
                    }
                }
            }
        }
        catch { } // corrupted object references — skip silently
    }

    public void OnWalkComplete()
    {
        // Pre-size the merged dict using the sum of all stripe sizes to avoid rehashing.
        int total = 0;
        for (int i = 0; i < StripeCount; i++) total += _stripes[i].Count;

        var bfs = new Dictionary<ulong, ParentSlots>(total);
        for (int i = 0; i < StripeCount; i++)
        {
            foreach (var (k, v) in _stripes[i]) bfs[k] = v;
            _stripes[i].Clear();
            _stripes[i] = null!; // release stripe backing array to GC
        }
        BfsMap   = bfs;
        // HotTypes dictionaries are already the final result — no merge needed.
        HotTypes = _hotTypes;
    }

    // Never called - IsThreadSafe = true
    public IHeapObjectConsumer CreateClone() => throw new NotSupportedException();
    public void MergeFrom(IHeapObjectConsumer other) { }
}
