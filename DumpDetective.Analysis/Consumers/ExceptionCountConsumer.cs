using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>Counts live exception instances per type name.</summary>
internal sealed class ExceptionCountConsumer : IHeapObjectConsumer
{
    public Dictionary<string, int> Totals { get; } = new(128, StringComparer.Ordinal);

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsException) return;
        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(Totals, meta.Name, out _);
        c++;
    }

    public void OnWalkComplete() { }
}
