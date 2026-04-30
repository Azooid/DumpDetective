using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Counts active async state machines per method name and accumulates the total backlog.
/// An object is counted when its <see cref="HeapTypeMeta.AsyncMethod"/> is non-null —
/// that field is set by <c>HeapWalker.BuildMeta</c> when the type name matches the
/// compiler-generated state machine pattern (e.g. <c>MyService+&lt;DoWorkAsync&gt;d__4</c>).
/// The outer method name is extracted from the compiler name so the report groups
/// all state machine instances for the same async method together.
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

        // Only count suspended (Awaiting) state machines in the backlog: state >= 0.
        // Completed (-1) and Initial (-2) state machines must not inflate the backlog total.
        // If the <>1__state field is absent or unreadable the state is "Unknown" —
        // matches AsyncStacksAnalyzer.ReadStateLabel which also returns "Unknown" in
        // those cases and does not count them in the backlog.
        try
        {
            var field = obj.Type?.GetFieldByName("<>1__state");
            if (field is not null && field.Read<int>(obj, interior: false) >= 0)
                BacklogTotal++;
        }
        catch { } // Unknown state on read failure — do not count
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new AsyncMethodConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (AsyncMethodConsumer)other;
        BacklogTotal += src.BacklogTotal;
        foreach (var (method, count) in src.MethodCounts)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(MethodCounts, method, out _);
            dst += count;
        }
    }
}
