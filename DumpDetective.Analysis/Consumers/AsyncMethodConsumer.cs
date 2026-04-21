using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Counts active async state machines per method name and accumulates the total backlog.
/// </summary>
internal sealed class AsyncMethodConsumer : IHeapObjectConsumer
{
    /// <summary>Per-method async state machine counts.</summary>
    public Dictionary<string, int> MethodCounts { get; } = new(512, StringComparer.Ordinal);

    public int BacklogTotal { get; private set; }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        var method = meta.AsyncMethod;
        if (method is null) return;

        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(MethodCounts, method, out _);
        c++;
        BacklogTotal++;
    }

    public void OnWalkComplete() { }
}
