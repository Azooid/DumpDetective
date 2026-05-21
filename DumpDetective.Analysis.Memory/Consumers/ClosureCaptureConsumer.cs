using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Collects compiler-generated closure display-class objects (types containing
/// <c>&lt;&gt;c__DisplayClass</c> or <c>&lt;&gt;c+&lt;</c>) during the single shared heap walk,
/// so <c>ClosureCaptureAnalyzer</c> can skip a second heap enumeration.
/// Stores up to 5 sample addresses per type for BFS retained-size computation.
/// </summary>
internal sealed class ClosureCaptureConsumer : IHeapObjectConsumer
{
    private const int MaxSampleAddrs = 5;
    private const int MaxGroups      = 200; // cap to bound memory

    /// <summary>Key = closure type name. Value = (declaring type, sample addrs, own size total).</summary>
    private readonly Dictionary<string, ClosureEntry> _byType = new(64, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ClosureEntry> ByType => _byType;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (string.IsNullOrEmpty(meta.Name)) return;
        if (!meta.Name.Contains("<>c__DisplayClass", StringComparison.Ordinal) &&
            !meta.Name.Contains("<>c+<", StringComparison.Ordinal)) return;

        long size = (long)obj.Size;

        if (!_byType.TryGetValue(meta.Name, out var entry))
        {
            if (_byType.Count >= MaxGroups) return; // safety cap
            entry = new ClosureEntry(ExtractDeclaring(meta.Name));
            _byType[meta.Name] = entry;
        }
        entry.Count++;
        entry.OwnSizeTotal += size;
        if (entry.SampleAddrs.Count < MaxSampleAddrs)
            entry.SampleAddrs.Add(obj.Address);
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new ClosureCaptureConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (ClosureCaptureConsumer)other;
        foreach (var (key, sv) in s._byType)
        {
            if (!_byType.TryGetValue(key, out var ev))
            {
                if (_byType.Count >= MaxGroups) continue;
                _byType[key] = ev = new ClosureEntry(sv.DeclaringType);
            }
            ev.Count       += sv.Count;
            ev.OwnSizeTotal += sv.OwnSizeTotal;
            foreach (var a in sv.SampleAddrs)
            {
                if (ev.SampleAddrs.Count < MaxSampleAddrs)
                    ev.SampleAddrs.Add(a);
            }
        }
    }

    private static string ExtractDeclaring(string closureTypeName)
    {
        int plus = closureTypeName.IndexOf('+');
        return plus > 0 ? closureTypeName[..plus] : closureTypeName;
    }

    internal sealed class ClosureEntry(string declaringType)
    {
        public string      DeclaringType = declaringType;
        public int         Count;
        public long        OwnSizeTotal;
        public List<ulong> SampleAddrs   = [];
    }
}

/// <summary>Cache key stored in ctx so <c>ClosureCaptureAnalyzer</c> gets a free hit.</summary>
internal sealed class ClosureCaptureConsumerResult(
    IReadOnlyDictionary<string, ClosureCaptureConsumer.ClosureEntry> byType)
{
    public IReadOnlyDictionary<string, ClosureCaptureConsumer.ClosureEntry> ByType { get; } = byType;
}
