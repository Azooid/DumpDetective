using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Tracks generation byte and object-count totals plus Frozen/POH details
/// needed to populate <see cref="HeapSnapshot"/> generation fields.
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
}
