using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Finds AssemblyLoadContext instances on the heap during the single shared walk,
/// so <c>ModuleListAnalyzer</c> can skip a second heap enumeration.
/// Reads the <c>_name</c>, <c>_isCollectible</c>, and <c>_loadedAssemblies</c>
/// fields directly from each matching object.
/// </summary>
internal sealed class AlcConsumer : IHeapObjectConsumer
{
    private const int MaxResults = 100;

    private readonly List<AlcRecord> _entries = [];
    public IReadOnlyList<AlcRecord> Entries => _entries;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (string.IsNullOrEmpty(meta.Name)) return;
        if (!meta.Name.Contains("AssemblyLoadContext", StringComparison.Ordinal)) return;
        if (_entries.Count >= MaxResults) return;

        string displayName  = meta.Name;
        bool   collectible  = false;
        int    asmCount     = 0;
        try
        {
            var nameField = obj.Type?.GetFieldByName("_name");
            if (nameField is not null)
                displayName = obj.ReadStringField("_name") ?? displayName;
            var collField = obj.Type?.GetFieldByName("_isCollectible");
            if (collField is not null)
                collectible = collField.Read<bool>(obj, interior: false);
            var loadedField = obj.Type?.GetFieldByName("_loadedAssemblies");
            if (loadedField is not null)
            {
                var listObj = loadedField.ReadObject(obj, interior: false);
                if (listObj.IsValid)
                {
                    var cntField = listObj.Type?.GetFieldByName("_size") ??
                                  listObj.Type?.GetFieldByName("_count");
                    if (cntField is not null)
                        asmCount = cntField.Read<int>(listObj, interior: false);
                }
            }
        }
        catch { }

        _entries.Add(new AlcRecord(obj.Address, displayName, collectible, asmCount));
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new AlcConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var s = (AlcConsumer)other;
        foreach (var e in s._entries)
        {
            if (_entries.Count >= MaxResults) break;
            _entries.Add(e);
        }
    }

    internal sealed record AlcRecord(ulong Address, string Name, bool IsCollectible, int AssemblyCount);
}

/// <summary>Cache key stored in ctx so <c>ModuleListAnalyzer</c> gets a free hit.</summary>
internal sealed class AlcConsumerResult(IReadOnlyList<AlcConsumer.AlcRecord> entries)
{
    public IReadOnlyList<AlcConsumer.AlcRecord> Entries { get; } = entries;
}
