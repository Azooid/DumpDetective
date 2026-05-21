using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Detects cache-like collection types (Dictionary, ConcurrentDictionary, MemoryCache, etc.)
/// and accumulates per-type entry counts and sizes during the single shared heap walk,
/// so <c>CachePatternsAnalyzer</c> can skip a second heap enumeration.
/// </summary>
internal sealed class CachePatternsConsumer : IHeapObjectConsumer
{
    private static readonly string[] CountFieldNames =
        ["_count", "m_count", "_size", "count", "_entryCount"];

    // Entry-count reading delegates to CachePatternMatcher.ReadEntryCount.

    /// <summary>Accumulated: (kind, instanceCount, totalEntries, totalSize, maxEntries, hasOversized)</summary>
    private readonly Dictionary<string, MutableEntry> _byType = new(64, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, MutableEntry> ByType => _byType;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (string.IsNullOrEmpty(meta.Name)) return;

        string? kind = CachePatternMatcher.Classify(meta.Name);
        if (kind is null) return;

        long entryCount = ReadEntryCount(obj);
        long size       = (long)obj.Size;

        if (!_byType.TryGetValue(meta.Name, out var e))
            _byType[meta.Name] = e = new MutableEntry(kind);

        e.InstanceCount++;
        e.TotalSize    += size;
        e.TotalEntries += entryCount;
        if (entryCount > e.MaxEntries)     e.MaxEntries = entryCount;
        if (e.SampleAddrs.Count < 5)       e.SampleAddrs.Add(obj.Address);
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new CachePatternsConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (CachePatternsConsumer)other;
        foreach (var (key, sv) in s._byType)
        {
            if (!_byType.TryGetValue(key, out var ev))
                _byType[key] = ev = new MutableEntry(sv.Kind);
            ev.InstanceCount += sv.InstanceCount;
            ev.TotalSize     += sv.TotalSize;
            ev.TotalEntries  += sv.TotalEntries;
            if (sv.MaxEntries > ev.MaxEntries) ev.MaxEntries = sv.MaxEntries;
            foreach (var a in sv.SampleAddrs)
                if (ev.SampleAddrs.Count < 5) ev.SampleAddrs.Add(a);
        }
    }

    private static long ReadEntryCount(in ClrObject obj) =>
        CachePatternMatcher.ReadEntryCount(obj);

    internal sealed class MutableEntry(string kind)
    {
        public string      Kind         = kind;
        public int         InstanceCount;
        public long        TotalEntries;
        public long        TotalSize;
        public long        MaxEntries;
        public List<ulong> SampleAddrs  = [];
    }
}

/// <summary>Cache key stored in ctx so <c>CachePatternsAnalyzer</c> gets a free hit.</summary>
internal sealed class CachePatternsConsumerResult(
    IReadOnlyDictionary<string, CachePatternsConsumer.MutableEntry> byType)
{
    public IReadOnlyDictionary<string, CachePatternsConsumer.MutableEntry> ByType { get; } = byType;
}
