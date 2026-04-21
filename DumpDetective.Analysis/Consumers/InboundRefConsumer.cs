using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>Accumulates per-object inbound reference counts for <see cref="HeapSnapshot.InboundCounts"/>.</summary>
internal sealed class InboundRefConsumer : IHeapObjectConsumer
{
    public Dictionary<ulong, int> InboundCounts { get; } = new(65536);

    private long _totalRefs;
    public long TotalRefs => _totalRefs;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        try
        {
            foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (refAddr == 0) continue;
                ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(InboundCounts, refAddr, out _);
                c++;
                _totalRefs++;
            }
        }
        catch { }
    }

    public void OnWalkComplete() { }
}
