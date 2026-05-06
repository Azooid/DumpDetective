using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

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
    public HeapAddrCount[]   TopAddrs          { get; private set; } = [];
    public InboundBucket[]   Histogram         { get; private set; } = [];

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

                // Select stripe by bits 3-10 of the target address.
                // Heap addresses are 8-byte aligned so bits 0-2 are always 0;
                // shifting right by 3 before masking gives 256 well-distributed buckets.
                int stripe = (int)((refAddr >> 3) & (StripeCount - 1));
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
        // Compute TopAddrs, Histogram, and totals DIRECTLY from the 256 stripes —
        // no merged Dictionary<ulong,int> needed.
        //
        // Old approach: created a 2.5 GB merged dict while 256 stripes (~2.5 GB) were
        // still alive → 5 GB peak. The merged dict was then held as InboundCounts but
        // HeapSnapshot.Create does NOT store it (it is immediately freed by ReleaseRaw).
        // The merged dict was pure overhead — never used for any analysis.
        //
        // New approach: iterate each stripe once to collect candidates (count >= 10)
        // and histogram counts, then free each stripe immediately. Peak = 2.5 GB (stripes
        // only) → drops to 0 as each is freed. No 2.5 GB merged dict ever allocated.

        var candidates  = new List<HeapAddrCount>(1024);
        var histCounts  = new int[HistogramBuckets.Length];
        long totalRefs  = 0;
        int  totalItems = 0;

        for (int i = 0; i < StripeCount; i++)
        {
            var stripe = _stripes[i];
            totalRefs  += _stripeRefs[i];
            totalItems += stripe.Count;

            foreach (var (addr, cnt) in stripe)
            {
                if (cnt >= 10) candidates.Add(new HeapAddrCount(addr, cnt));

                // Histogram — buckets are non-overlapping so break on first match.
                for (int b = 0; b < HistogramBuckets.Length; b++)
                {
                    if (cnt >= HistogramBuckets[b].Lo && cnt <= HistogramBuckets[b].Hi)
                    {
                        histCounts[b]++;
                        break;
                    }
                }
            }

            // Free this stripe immediately — never needed again.
            stripe.Clear();
            _stripes[i] = null!;
        }

        // Sort candidates descending, keep top 50.
        candidates.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        TopAddrs = candidates.Count <= 50
            ? candidates.ToArray()
            : candidates.GetRange(0, 50).ToArray();

        var hist = new InboundBucket[HistogramBuckets.Length];
        for (int b = 0; b < HistogramBuckets.Length; b++)
            hist[b] = new InboundBucket(HistogramBuckets[b].Lo, HistogramBuckets[b].Hi, histCounts[b]);
        Histogram = hist;

        TotalRefs         = totalRefs;
        InboundCountsSize = totalItems;

        // InboundCounts is never stored in HeapSnapshot and is freed immediately by
        // ReleaseRaw(). Avoid the 2.5 GB allocation entirely — use an empty sentinel.
        InboundCounts = [];
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
