using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Collects large objects (size ≥ configurable threshold, default 85 000 B) during the
/// single shared heap walk.  Results are stored in <c>ctx</c> via
/// <c>SetAnalysis&lt;LargeObjectsConsumerResult&gt;</c> so <c>LargeObjectsAnalyzer</c>
/// can skip a second heap enumeration for the common case (no custom threshold / filter).
/// </summary>
internal sealed class LargeObjectsConsumer : IHeapObjectConsumer
{
    private readonly long _minSize;
    private readonly List<(string TypeName, string ElemType, long Size, ulong Address)> _objects = [];

    public IReadOnlyList<(string TypeName, string ElemType, long Size, ulong Address)> Objects => _objects;

    public LargeObjectsConsumer(long minSize = 85_000)
    {
        _minSize = minSize;
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        long size = (long)obj.Size;
        if (size < _minSize) return;
        if (string.IsNullOrEmpty(meta.Name) || obj.Type is null) return;
        string elemType = obj.Type.IsArray ? (obj.Type.ComponentType?.Name ?? "?") : "";
        _objects.Add((meta.Name, elemType, size, obj.Address));
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new LargeObjectsConsumer(_minSize);

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (LargeObjectsConsumer)other;
        _objects.AddRange(s._objects);
    }
}

/// <summary>Cache key stored in ctx so analyzers can retrieve pre-collected large objects.</summary>
internal sealed class LargeObjectsConsumerResult(
    IReadOnlyList<(string TypeName, string ElemType, long Size, ulong Address)> objects)
{
    public IReadOnlyList<(string TypeName, string ElemType, long Size, ulong Address)> Objects { get; } = objects;
}
