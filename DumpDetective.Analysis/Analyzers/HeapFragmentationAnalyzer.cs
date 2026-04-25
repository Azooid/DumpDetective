using DumpDetective.Analysis.Consumers;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Measures managed heap fragmentation by walking every segment and classifying
/// each object as live or free, accumulating per-segment live/free byte ratios
/// and a free-hole size distribution histogram.
/// Uses a single parallel <see cref="HeapWalker"/> pass via <see cref="Consumers.FragmentationConsumer"/>
/// so all segments are walked concurrently — the most expensive part of this
/// analyzer on large heaps is the ~250 GB of I/O reads from ClrMD.
/// Pinned handle counts per segment are computed from <c>EnumerateHandles</c> before
/// the walk so they can be attached to the <see cref="HeapSegmentInfo"/> results.
/// </summary>
public sealed class HeapFragmentationAnalyzer
{
    public HeapFragmentationData Analyze(DumpContext ctx)
    {
        var (segments, distribution) = ScanCombined(ctx);
        return new HeapFragmentationData(segments, distribution);
    }

    /// <summary>
    /// Single parallel pass over all heap segments via <see cref="HeapWalker"/> that fills
    /// both per-segment live/free byte counts and the free-hole size distribution.
    /// </summary>
    private static (IReadOnlyList<HeapSegmentInfo> Segments, IReadOnlyList<FreeHoleBucket> Distribution)
        ScanCombined(DumpContext ctx)
    {
        // Count pinned handles per segment address
        var pinnedCounts = new Dictionary<ulong, int>();
        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            if (h.HandleKind != ClrHandleKind.Pinned || h.Object == 0) continue;
            var seg = ctx.Heap.GetSegmentByAddress(h.Object);
            if (seg is not null)
            {
                ref int c = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(pinnedCounts, seg.Address, out _);
                c++;
            }
        }

        // Single parallel heap walk via HeapWalker + FragmentationConsumer
        var consumer = new FragmentationConsumer(ctx.Heap, ctx.Heap.Segments, ctx.Heap.FreeType);

        CommandBase.RunStatus("Measuring fragmentation...", update =>
            HeapWalker.Walk(ctx.Heap, [consumer],
                CommandBase.StatusProgress(update)));

        // Apply pinned counts after walk
        foreach (var (addr, count) in pinnedCounts)
            if (consumer.SegData.TryGetValue(addr, out var s))
                s.PinnedCount = count;

        var segments = consumer.SegData.Values
            .Where(s => s.CommittedBytes > 0)
            .OrderByDescending(s => s.CommittedBytes > 0 ? s.FreeBytes * 100.0 / s.CommittedBytes : 0)
            .Select(s => new HeapSegmentInfo(s.Kind, s.Address, s.CommittedBytes, s.LiveBytes, s.FreeBytes, s.PinnedCount))
            .ToList();

        var distribution = consumer.Buckets
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                string label = kv.Key switch
                {
                    0 => "< 128 B",
                    1 => "128 B – 1 KB",
                    2 => "1 KB – 4 KB",
                    3 => "4 KB – 64 KB",
                    4 => "64 KB – 1 MB",
                    _ => "≥ 1 MB",
                };
                return new FreeHoleBucket(label, kv.Value.Count, kv.Value.Size, kv.Key);
            })
            .ToList();

        return (segments, distribution);
    }
}
