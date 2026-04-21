using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Collects all heap-resident async state-machine objects, recording the outer
/// method name (<c>meta.AsyncMethod</c>), suspension state, and address.
/// </summary>
public sealed class AsyncStacksAnalyzer : IHeapObjectConsumer
{
    private List<StateMachineEntry>? _entries;
    private int _backlogTotal;
    private AsyncStacksData? _result;

    public AsyncStacksData? Result => _result;

    internal void Reset()
    {
        _entries      = new List<StateMachineEntry>();
        _backlogTotal = 0;
        _result       = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        var method = meta.AsyncMethod;
        if (method is null || _entries is null) return;

        _entries.Add(new StateMachineEntry(method, ReadStateLabel(obj), obj.Address));
        _backlogTotal++;
    }

    public void OnWalkComplete()
        => _result = new AsyncStacksData((_entries ?? []).ToList(), _backlogTotal);

    // ── Command entry point ───────────────────────────────────────────────────

    public AsyncStacksData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<AsyncStacksData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning async state machines...", () =>
            HeapWalker.Walk(ctx.Heap, [this]));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>&lt;&gt;1__state</c>: −2 = Initial, −1 = Completed/Faulted, ≥0 = Awaiting.
    /// </summary>
    private static string ReadStateLabel(in ClrObject obj)
    {
        try
        {
            var field = obj.Type?.GetFieldByName("<>1__state");
            if (field is null) return "Unknown";
            int state = field.Read<int>(obj, interior: false);
            return state switch
            {
                -2 => "Initial",
                -1 => "Completed",
                 _ => "Awaiting",
            };
        }
        catch { return "Unknown"; }
    }
}
