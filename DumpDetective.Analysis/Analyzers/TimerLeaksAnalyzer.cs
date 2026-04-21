using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Collects full detail for all live <c>System.Threading.Timer</c> instances.
/// Implements <see cref="IHeapObjectConsumer"/> so it can be driven by
/// <see cref="HeapWalker"/> — either from <c>DumpCollector.CollectHeapObjectsCombined</c>
/// (pre-warms the cache) or from its own standalone heap walk via <see cref="Analyze"/>.
/// </summary>
public sealed class TimerLeaksAnalyzer : IHeapObjectConsumer
{
    private ClrRuntime? _runtime;
    private List<TimerItem>? _items;
    private TimerLeaksData? _result;

    /// <summary>Result built by <see cref="IHeapObjectConsumer.OnWalkComplete"/>. Non-null after the walk.</summary>
    public TimerLeaksData? Result => _result;

    /// <summary>
    /// Resets accumulator state before a walk. <paramref name="runtime"/> is used to
    /// resolve timer callback method names; pass <see langword="null"/> to skip resolution.
    /// </summary>
    internal void Reset(ClrRuntime? runtime = null)
    {
        _runtime = runtime;
        _items   = new List<TimerItem>();
        _result  = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsTimer || _items is null) return;
        var (cb, module) = _runtime is not null ? ResolveCallback(obj, _runtime) : ("", "");
        _items.Add(new TimerItem(
            meta.Name,
            obj.Address,
            (long)obj.Size,
            cb,
            module,
            ReadTimerLong(obj, "_dueTime"),
            ReadTimerLong(obj, "_period")));
    }

    public void OnWalkComplete()
        => _result = new TimerLeaksData((_items ?? []).ToList());

    // ── Command entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the pre-warmed analysis from <paramref name="ctx"/> if available,
    /// otherwise performs a standalone heap walk.
    /// </summary>
    public TimerLeaksData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<TimerLeaksData>() is { } cached) return cached;

        Reset(ctx.Runtime);
        CommandBase.RunStatus("Scanning timer objects...", () =>
            HeapWalker.Walk(ctx.Heap, [this]));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Callback, string Module) ResolveCallback(ClrObject obj, ClrRuntime runtime)
    {
        try
        {
            var cb = obj.ReadObjectField("m_callback");
            if (cb.IsNull || !cb.IsValid) return ("", "");
            ulong ptr = cb.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return (cb.Type?.Name ?? "", "");
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return (cb.Type?.Name ?? "", "");
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            return ($"{typePart}{m.Name}", Path.GetFileName(m.Type?.Module?.Name ?? ""));
        }
        catch { return ("", ""); }
    }

    private static long ReadTimerLong(ClrObject obj, string field)
    {
        try { return obj.ReadField<long>(field); } catch { }
        try { return obj.ReadField<int>(field); }  catch { }
        return -1;
    }
}
