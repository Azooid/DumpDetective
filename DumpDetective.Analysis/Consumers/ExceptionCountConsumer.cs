using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Counts live exception instances per type name.
/// Only objects where <see cref="HeapTypeMeta.IsException"/> is <see langword="true"/>
/// are counted — that flag is set by <c>HeapWalker.BuildMeta</c> when the type
/// derives from <c>System.Exception</c>.
/// These are heap-resident instances, not necessarily exceptions currently being thrown.
/// The counts feed <c>ExceptionAnalysisCommand</c>'s summary table.
/// </summary>
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

    public IHeapObjectConsumer CreateClone() => new ExceptionCountConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (ExceptionCountConsumer)other;
        foreach (var (name, count) in src.Totals)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(Totals, name, out _);
            dst += count;
        }
    }
}
