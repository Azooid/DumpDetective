using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Tracks generation byte and object-count totals plus Frozen/POH details
/// needed to populate <see cref="HeapSnapshot"/> generation fields.
/// Also counts objects that exceed the LOH allocation threshold (≥ 85 000 B)
/// so <c>GenSummaryCommand</c> can report how many large objects exist even
/// outside the actual LOH segment (e.g. large POH / pinned arrays).
/// Generation is resolved the same way as <see cref="TypeStatsConsumer"/>:
/// segment kind first, then sub-range check for ephemeral segments.
/// </summary>
internal sealed class GenCounterConsumer : IHeapObjectConsumer
{
    public long Gen0Bytes { get; private set; }
    public long Gen1Bytes { get; private set; }
    public long Gen2Bytes { get; private set; }
    public long LohBytes  { get; private set; }
    public long PohBytes  { get; private set; }

    public long Gen0ObjCount { get; private set; }
    public long Gen1ObjCount { get; private set; }
    public long Gen2ObjCount { get; private set; }

    public long FrozenObjCount { get; private set; }
    public long FrozenObjSize  { get; private set; }
    public long PohObjCount    { get; private set; }
    public long PohObjSize     { get; private set; }

    /// <summary>Count of objects with size ≥ 85 000 bytes (LOH allocation threshold).</summary>
    public long LohThresholdObjectCount { get; private set; }
    /// <summary>Total bytes of all objects with size ≥ 85 000 bytes.</summary>
    public long LohThresholdLiveBytes   { get; private set; }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        long size = (long)obj.Size;

        if (size >= 85_000)
        {
            LohThresholdObjectCount++;
            LohThresholdLiveBytes += size;
        }

        var seg = heap.GetSegmentByAddress(obj.Address);

        switch (seg?.Kind)
        {
            case GCSegmentKind.Generation0:
                Gen0Bytes += size; Gen0ObjCount++; break;
            case GCSegmentKind.Generation1:
                Gen1Bytes += size; Gen1ObjCount++; break;
            case GCSegmentKind.Generation2:
                Gen2Bytes += size; Gen2ObjCount++; break;
            case GCSegmentKind.Large:
                LohBytes += size; break;
            case GCSegmentKind.Pinned:
                PohBytes += size; PohObjCount++; PohObjSize += size; break;
            case GCSegmentKind.Frozen:
                FrozenObjCount++; FrozenObjSize += size; break;
            case GCSegmentKind.Ephemeral when seg is not null:
                if      (seg.Generation0.Contains(obj.Address)) { Gen0Bytes += size; Gen0ObjCount++; }
                else if (seg.Generation1.Contains(obj.Address)) { Gen1Bytes += size; Gen1ObjCount++; }
                else                                            { Gen2Bytes += size; Gen2ObjCount++; }
                break;
            default:
                Gen2Bytes += size; Gen2ObjCount++; break;
        }
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new GenCounterConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (GenCounterConsumer)other;
        Gen0Bytes += s.Gen0Bytes; Gen0ObjCount += s.Gen0ObjCount;
        Gen1Bytes += s.Gen1Bytes; Gen1ObjCount += s.Gen1ObjCount;
        Gen2Bytes += s.Gen2Bytes; Gen2ObjCount += s.Gen2ObjCount;
        LohBytes  += s.LohBytes;
        PohBytes  += s.PohBytes;  PohObjCount  += s.PohObjCount;  PohObjSize  += s.PohObjSize;
        FrozenObjCount += s.FrozenObjCount;                        FrozenObjSize += s.FrozenObjSize;
        LohThresholdObjectCount += s.LohThresholdObjectCount;
        LohThresholdLiveBytes   += s.LohThresholdLiveBytes;
    }
}
