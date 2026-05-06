using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Memory.Consumers;

using DumpDetective.Analysis.Memory.Analyzers; // ParentSlots + ParentSlots.Max

/// <summary>
/// Builds the BfsMap (child to parent slots, written to a temp disk file) and HotAddrTypes
/// (hot address to referencing type counts) for SharedReferrerCache.
/// Uses 256-stripe locking on BfsMap — 1x memory instead of 8x clones
/// (~10 GB -> ~1.3 GB peak during the referrer walk).
/// After OnWalkComplete the stripes are flushed to a DiskBackedParentMap so the
/// in-memory dict (~2.56 GB) is never built — only ~1.28 GB is written to disk and
/// the OS pages only the BFS-hot subset (~72 KB) into RAM.
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

    // Exposed after OnWalkComplete as a disk-backed map and HotTypes dict.
    public DiskBackedParentMap?                               ParentMap { get; private set; }
    public Dictionary<ulong, Dictionary<string, int>> HotTypes  { get; private set; } = [];

    private readonly string _parentMapPath;

    public ReferrerConsumer(int bfsCapacity, HashSet<ulong> hotAddrs, string parentMapPath)
    {
        _parentMapPath = parentMapPath;
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
                // Same stripe-lock pattern as InboundRefConsumer — bits 3-10 of the
                // child address select the shard; 256 shards keep contention < 3%.
                int stripe = (int)((refAddr >> 3) & (StripeCount - 1));
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
        // Write all stripes to a sorted disk file instead of merging into a 2.56 GB dict.
        // DiskBackedParentMap.Write consumes and clears each stripe in-place.
        ParentMap = DiskBackedParentMap.Write(_stripes, _parentMapPath);
        // HotTypes dictionaries are already the final result — no merge needed.
        HotTypes  = _hotTypes;
    }

    // Never called - IsThreadSafe = true
    public IHeapObjectConsumer CreateClone() => throw new NotSupportedException();
    public void MergeFrom(IHeapObjectConsumer other) { }
}
