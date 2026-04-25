using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates string value groups for <see cref="HeapSnapshot.StringGroups"/>.
/// Uses 256-stripe locking — 1× memory instead of 8× clones (~2-4 GB saved on large heaps).
/// </summary>
internal sealed class StringGroupConsumer : IHeapObjectConsumer
{
    public bool IsThreadSafe => true;

    private const int StripeCount = 256;

    private readonly object[]                                         _locks;
    private readonly Dictionary<string, (int Count, long TotalSize)>[] _stripes;
    private long _totalCount;
    private long _totalSize;

    public long TotalStringCount => _totalCount;
    public long TotalStringSize  => _totalSize;

    public Dictionary<string, (int Count, long TotalSize)> StringGroups { get; private set; } = [];

    public StringGroupConsumer()
    {
        _locks   = new object[StripeCount];
        _stripes = new Dictionary<string, (int, long)>[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            _locks[i]   = new object();
            _stripes[i] = new Dictionary<string, (int, long)>(256, StringComparer.Ordinal);
        }
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.Name != "System.String") return;

        long size = (long)obj.Size;
        Interlocked.Increment(ref _totalCount);
        Interlocked.Add(ref _totalSize, size);

        try
        {
            var val = obj.AsString(maxLength: 512) ?? string.Empty;
            int stripe = (val.Length > 0 ? val[0] : 0) & (StripeCount - 1);
            lock (_locks[stripe])
            {
                ref var sg = ref CollectionsMarshal.GetValueRefOrAddDefault(_stripes[stripe], val, out bool existed);
                sg = existed ? (sg.Count + 1, sg.TotalSize + size) : (1, size);
            }
        }
        catch { }
    }

    public void OnWalkComplete()
    {
        int total = 0;
        for (int i = 0; i < StripeCount; i++) total += _stripes[i].Count;
        var merged = new Dictionary<string, (int, long)>(total, StringComparer.Ordinal);
        for (int i = 0; i < StripeCount; i++)
        {
            foreach (var kv in _stripes[i]) merged[kv.Key] = kv.Value;
            _stripes[i].Clear();
        }
        StringGroups = merged;
    }

    // Never called — IsThreadSafe = true
    public IHeapObjectConsumer CreateClone() => new StringGroupConsumer();
    public void MergeFrom(IHeapObjectConsumer other) { }
}
