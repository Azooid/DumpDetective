using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates per-object inbound reference counts for <see cref="HeapSnapshot.InboundCounts"/>.
/// Uses a 256-stripe lock array so all 8 parallel bucket workers write to independent shards —
/// no cloning (1× memory instead of 8×), near-zero contention (P(collision) ≈ 7/256 ≈ 2.7%).
///
/// After <see cref="OnWalkComplete"/> the raw 80M-entry dictionary is held temporarily in
/// <see cref="InboundCounts"/>. Call <see cref="ReleaseRaw"/> once hot-addr extraction is done
/// (inside <c>SharedReferrerCache.Build</c>) to free the ~1.9 GB backing array.
/// </summary>
internal sealed class InboundRefConsumer : IHeapObjectConsumer
{
    public bool IsThreadSafe => true;

    private const int StripeCount = 256; // must be power of 2
    private readonly object[]                 _locks;
    private readonly Dictionary<ulong, int>[] _stripes;
    private readonly long[]                   _stripeRefs;

    public Dictionary<ulong, int> InboundCounts { get; private set; } = [];
    public long TotalRefs { get; private set; }

    // Pre-distilled summary computed in OnWalkComplete — survives after ReleaseRaw().
    public int                        InboundCountsSize { get; private set; }
    public (ulong Addr, int Count)[]  TopAddrs          { get; private set; } = [];
    public (int Lo, int Hi, int Count)[] Histogram      { get; private set; } = [];

    private static readonly (int Lo, int Hi)[] HistogramBuckets =
    [
        (10,    49),
        (50,    99),
        (100,   499),
        (500,   999),
        (1_000, 9_999),
        (10_000, int.MaxValue),
    ];

    public InboundRefConsumer()
    {
        _locks      = new object[StripeCount];
        _stripes    = new Dictionary<ulong, int>[StripeCount];
        _stripeRefs = new long[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            _locks[i]   = new object();
            _stripes[i] = new Dictionary<ulong, int>(1 << 10);
        }
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        try
        {
            foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (refAddr == 0) continue;

                // Select stripe by low 8 bits of the target address.
                // Heap addresses are 8-byte aligned so bits 0-2 are always 0;
                // bits 3-10 give 256 well-distributed buckets across all segments.
                int stripe = (int)(refAddr & (StripeCount - 1));
                lock (_locks[stripe])
                {
                    // GetValueRefOrAddDefault returns a ref into the dict's internal
                    // storage — safe here because the lock prevents concurrent resize.
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(_stripes[stripe], refAddr, out _);
                    c++;
                    _stripeRefs[stripe]++;
                }
            }
        }
        catch { } // corrupted object references — skip silently
    }

    public void OnWalkComplete()
    {
        // Count total entries across all stripes to pre-size the merged dict,
        // avoiding incremental resizes during the O(N) merge loop.
        int totalEntries = 0;
        for (int i = 0; i < StripeCount; i++) totalEntries += _stripes[i].Count;

        var merged = new Dictionary<ulong, int>(totalEntries);
        long totalRefs = 0;
        for (int i = 0; i < StripeCount; i++)
        {
            foreach (var (k, v) in _stripes[i]) merged[k] = v;
            totalRefs    += _stripeRefs[i];
            _stripes[i].Clear();
            _stripes[i] = null!; // null the ref so the stripe backing array is GC-eligible
        }
        InboundCounts     = merged;
        TotalRefs         = totalRefs;
        InboundCountsSize = merged.Count;

        // Pre-distil top-50 addrs (used by HighRefsAnalyzer + SharedReferrerCache.Build).
        // Stored as a tiny array so InboundCounts (~1.9 GB) can be released immediately
        // after hot-address extraction without losing the data needed for reports.
        TopAddrs = merged
            .Where(kv => kv.Value >= 10)
            .OrderByDescending(kv => kv.Value)
            .Take(50)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();

        // Pre-build the ref-count histogram so HighRefsAnalyzer never needs the
        // raw 80 M-entry dict for histogram computation — O(N) done once here.
        var hist = new (int Lo, int Hi, int Count)[HistogramBuckets.Length];
        for (int b = 0; b < HistogramBuckets.Length; b++)
        {
            int lo = HistogramBuckets[b].Lo, hi = HistogramBuckets[b].Hi;
            int cnt = 0;
            foreach (var v in merged.Values)
                if (v >= lo && v <= hi) cnt++;
            hist[b] = (lo, hi, cnt);
        }
        Histogram = hist;
    }

    /// <summary>
    /// Releases the raw ~1.9 GB InboundCounts dictionary.
    /// Call once hot-address extraction is complete (in SharedReferrerCache.Build).
    /// TopAddrs and Histogram remain available afterwards.
    /// </summary>
    public void ReleaseRaw()
    {
        InboundCounts.Clear();
        InboundCounts = [];
    }

    // Never called — IsThreadSafe = true
    public IHeapObjectConsumer CreateClone() => new InboundRefConsumer();
    public void MergeFrom(IHeapObjectConsumer other) { }
}
