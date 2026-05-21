using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace Example.DumpDetective;

internal struct NamespaceHeapStats
{
    public long Size;
    public long Count;
}

internal sealed class NamespaceHeapCache
{
    public NamespaceHeapCache(IReadOnlyDictionary<string, NamespaceHeapStats> totals, long totalSize, long totalObjects)
    {
        Totals = totals;
        TotalSize = totalSize;
        TotalObjects = totalObjects;
    }

    public IReadOnlyDictionary<string, NamespaceHeapStats> Totals { get; }
    public long TotalSize { get; }
    public long TotalObjects { get; }

    public static NamespaceHeapCache Build(DumpContext ctx)
    {
        var consumer = new NamespaceHeapConsumer();

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid) continue;

            var type = obj.Type;
            if (type is null) continue;

            consumer.Accumulate(type.MethodTable, type.Name ?? string.Empty, (long)obj.Size);
        }

        return consumer.ToCache();
    }

    internal static string TopLevelNamespace(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "<unknown>";

        int dot = typeName.IndexOf('.');
        if (dot <= 0) return typeName;

        string top = typeName[..dot];
        if (top.Length > 0 && top[0] == '<') return "<compiler-generated>";

        return top;
    }
}

internal sealed class NamespaceHeapConsumer : IHeapObjectConsumer
{
    private readonly Dictionary<string, NamespaceHeapStats> _totals = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> _namespaceByMethodTable = [];

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
        => Accumulate(meta.MT, meta.Name, (long)obj.Size);

    public void OnWalkComplete()
    {
    }

    public IHeapObjectConsumer CreateClone() => new NamespaceHeapConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        foreach (var pair in ((NamespaceHeapConsumer)other)._totals)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_totals, pair.Key, out _);
            entry.Size += pair.Value.Size;
            entry.Count += pair.Value.Count;
        }
    }

    internal void Accumulate(ulong methodTable, string typeName, long size)
    {
        if (!_namespaceByMethodTable.TryGetValue(methodTable, out string? ns))
        {
            ns = NamespaceHeapCache.TopLevelNamespace(typeName);
            _namespaceByMethodTable[methodTable] = ns;
        }

        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_totals, ns, out _);
        entry.Size += size;
        entry.Count += 1;
    }

    internal NamespaceHeapCache ToCache()
    {
        var copy = new Dictionary<string, NamespaceHeapStats>(_totals.Count, StringComparer.Ordinal);
        long totalSize = 0;
        long totalObjects = 0;

        foreach (var pair in _totals)
        {
            copy[pair.Key] = pair.Value;
            totalSize += pair.Value.Size;
            totalObjects += pair.Value.Count;
        }

        return new NamespaceHeapCache(copy, totalSize, totalObjects);
    }
}