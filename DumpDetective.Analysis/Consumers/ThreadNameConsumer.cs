using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates the managed-thread-id → name map for <c>ThreadAnalysisAnalyzer</c>
/// and <c>DeadlockAnalyzer</c>.  Pre-populated during <c>CollectHeapObjectsCombined</c>
/// and stored as a <c>ThreadNameMap</c> via <c>DumpContext.PreloadAnalysis</c>.
/// Both analyzers call <c>GetOrCreateAnalysis&lt;ThreadNameMap&gt;</c> which returns the
/// pre-built result instantly instead of triggering a second heap walk.
/// </summary>
internal sealed class ThreadNameConsumer : IHeapObjectConsumer
{
    public readonly ThreadNameMap Map = new();

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsThread) return;
        try
        {
            int mgdId = obj.ReadField<int>("_managedThreadId");
            if (mgdId <= 0) return;
            string? name = null;
            try { name = obj.ReadStringField("_name"); } catch { }
            if (!string.IsNullOrEmpty(name))
                Map[mgdId] = name!;
        }
        catch { }
    }

    public void OnWalkComplete() { }
}
