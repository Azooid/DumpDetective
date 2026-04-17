using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Detects event handler leaks by counting the number of subscribers on every
/// delegate-typed instance field on non-system types.
/// </summary>
public sealed class EventAnalysisAnalyzer : IHeapObjectConsumer
{
    private Dictionary<(string, string), int>? _totals;
    private EventAnalysisData? _result;

    public EventAnalysisData? Result => _result;

    internal void Reset()
    {
        _totals = new Dictionary<(string, string), int>(4096);
        _result = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.DelegateFields.Length == 0 || _totals is null) return;

        foreach (var field in meta.DelegateFields)
        {
            try
            {
                var delVal = field.Field.ReadObject(obj.Address, false);
                if (delVal.IsNull || !delVal.IsValid) continue;
                int subs = CountSubscribers(delVal);
                if (subs <= 0) continue;

                var key = (meta.Name, field.Name);
                ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault<(string, string), int>(_totals, key, out bool existed);
                existing = existed ? existing + subs : subs;
            }
            catch { }
        }
    }

    public void OnWalkComplete()
    {
        if (_totals is null || _totals.Count == 0)
        {
            _result = new EventAnalysisData([]);
            return;
        }

        var groups = new List<EventLeakGroup>(_totals.Count);
        foreach (var kv in _totals)
            groups.Add(new EventLeakGroup(kv.Key.Item1, kv.Key.Item2, kv.Value));
        groups.Sort(static (a, b) => b.Subscribers.CompareTo(a.Subscribers));

        _result = new EventAnalysisData(groups);
    }

    // ── Command entry point ───────────────────────────────────────────────────

    public EventAnalysisData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<EventAnalysisData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning event handlers...", () =>
            HeapWalker.Walk(ctx.Heap, [this]));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountSubscribers(ClrObject del) => HeapWalker.CountSubscribers(del);
}
