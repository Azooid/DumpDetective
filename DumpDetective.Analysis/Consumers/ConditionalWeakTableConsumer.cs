using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Typed wrapper for the pre-built CWT list, used as the cache key in
/// <c>DumpContext.SetAnalysis&lt;CwtData&gt;</c>.
/// </summary>
internal sealed class CwtData(IReadOnlyList<CwtInstanceInfo> tables)
{
    public IReadOnlyList<CwtInstanceInfo> Tables { get; } = tables;
}

/// <summary>
/// Accumulates <see cref="CwtInstanceInfo"/> entries for <c>WeakRefsAnalyzer</c>.
/// Pre-populated during <c>CollectHeapObjectsCombined</c> (full mode) and cached via
/// <c>DumpContext.SetAnalysis&lt;CwtData&gt;</c>.
/// </summary>
internal sealed class ConditionalWeakTableConsumer : IHeapObjectConsumer
{    private readonly List<CwtInstanceInfo> _entries = [];

    public IReadOnlyList<CwtInstanceInfo> Entries => _entries;

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsCwt) return;

        int entryCount = 0;
        try
        {
            var container = obj.ReadObjectField("_container");
            if (!container.IsNull && container.IsValid)
            {
                var entries = container.ReadObjectField("_entries");
                if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                    entryCount = entries.AsArray().Length;
            }
        }
        catch { }

        if (entryCount == 0)
        {
            try
            {
                var entries = obj.ReadObjectField("_entries");
                if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                    entryCount = entries.AsArray().Length;
            }
            catch { }
        }

        string typeParam = meta.Name.Contains('[') ? meta.Name[meta.Name.IndexOf('[')..] : "";
        _entries.Add(new CwtInstanceInfo(typeParam, entryCount));
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new ConditionalWeakTableConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (ConditionalWeakTableConsumer)other;
        _entries.AddRange(src._entries);
    }
}
