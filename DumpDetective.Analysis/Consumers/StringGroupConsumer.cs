using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>Accumulates string value groups for <see cref="HeapSnapshot.StringGroups"/>.</summary>
internal sealed class StringGroupConsumer : IHeapObjectConsumer
{
    public Dictionary<string, (int Count, long TotalSize)> StringGroups { get; } =
        new(StringComparer.Ordinal);

    private long _totalCount;
    private long _totalSize;
    public long TotalStringCount => _totalCount;
    public long TotalStringSize  => _totalSize;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.Name != "System.String") return;

        long size = (long)obj.Size;
        _totalCount++;
        _totalSize += size;

        try
        {
            var val = obj.AsString(maxLength: 512) ?? string.Empty;
            ref var sg = ref CollectionsMarshal.GetValueRefOrAddDefault(StringGroups, val, out bool existed);
            if (existed) sg = (sg.Count + 1, sg.TotalSize + size);
            else         sg = (1, size);
        }
        catch { }
    }

    public void OnWalkComplete() { }
}
