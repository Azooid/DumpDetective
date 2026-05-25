using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

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

        // For System.Threading.Timer the real data lives on the inner TimerQueueTimer.
        // On .NET Framework: Timer.m_timer → TimerHolder.m_timer → TimerQueueTimer (two hops)
        // On .NET Core:      Timer.m_timer → TimerQueueTimer (one hop)
        // For System.Threading.TimerQueueTimer / System.Timers.Timer it lives directly on the object.
        ClrObject timerObj = obj;
        if (meta.Name == "System.Threading.Timer")
        {
            var hop1 = Deref(obj, "m_timer");
            if (hop1.IsValid)
            {
                // If we landed on TimerHolder, go one more level
                timerObj = hop1.Type?.Name == "System.Threading.TimerHolder"
                    ? Deref(hop1, "m_timer")
                    : hop1;
            }
        }

        var (cb, module) = _runtime is not null ? ResolveCallback(timerObj, meta.Name, _runtime) : ("", "");
        _items.Add(new TimerItem(
            meta.Name,
            obj.Address,
            (long)obj.Size,
            cb,
            module,
            ReadTimerLong(timerObj, "_dueTime", "m_dueTime"),
            ReadTimerLong(timerObj, "_period",  "m_period")));
    }

    public void OnWalkComplete()
        => _result = new TimerLeaksData((_items ?? []).ToList());

    public IHeapObjectConsumer CreateClone()
    {
        var c = new TimerLeaksAnalyzer();
        c.Reset(_runtime);
        return c;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (TimerLeaksAnalyzer)other;
        if (src._items is not null)
            (_items ??= []).AddRange(src._items);
    }

    // ── Command entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the pre-warmed analysis from <paramref name="ctx"/> if available,
    /// otherwise performs a standalone heap walk.
    /// </summary>
    public TimerLeaksData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<TimerLeaksData>() is { } cached) return cached;

        Reset(ctx.Runtime);
        CommandBase.RunStatus("Scanning timer objects...", update =>
            HeapWalker.Walk(ctx.Heap, [this], CommandBase.StatusProgress(update), sequentialOnly: ctx.IsCoreRuntime));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Safely follow one object-reference field; returns default (invalid) on any failure.</summary>
    private static ClrObject Deref(ClrObject obj, string fieldName)
    {
        try
        {
            if (obj.Type?.GetFieldByName(fieldName) is null) return default;
            var child = obj.ReadObjectField(fieldName);
            return child.IsValid && !child.IsNull ? child : default;
        }
        catch { return default; }
    }

    private static (string Callback, string Module) ResolveCallback(
        ClrObject timerObj, string typeName, ClrRuntime runtime)
    {
        try
        {
            // Field name differs by type:
            //   TimerQueueTimer  → m_timerCallback  (.NET Fx + Core)
            //   System.Timers.Timer → callback
            ClrObject cb = default;
            foreach (var fn in (ReadOnlySpan<string>)["m_timerCallback", "m_callback", "callback"])
            {
                if (timerObj.Type?.GetFieldByName(fn) is null) continue;
                var candidate = timerObj.ReadObjectField(fn);
                if (candidate.IsValid && !candidate.IsNull) { cb = candidate; break; }
            }
            if (!cb.IsValid || cb.IsNull) return ("", "");

            // Try _methodPtr (NativeInt on .NET Fx/Core delegates)
            ulong ptr = 0;
            foreach (var pf in (ReadOnlySpan<string>)["_methodPtr", "_methodPtrAux"])
            {
                if (cb.Type?.GetFieldByName(pf) is null) continue;
                try { ptr = cb.ReadField<ulong>(pf); } catch { try { ptr = (ulong)cb.ReadField<uint>(pf); } catch { } }
                if (ptr != 0) break;
            }

            if (ptr == 0)
            {
                // _methodPtr is 0 — fall back to the delegate target type as a hint
                var targetField = cb.Type?.GetFieldByName("_target");
                string targetHint = "";
                if (targetField?.IsObjectReference == true)
                {
                    try
                    {
                        var tgt = targetField.ReadObject(cb, false);
                        if (tgt.IsValid && !tgt.IsNull && tgt.Type is not null)
                            targetHint = tgt.Type.Name ?? "";
                    }
                    catch { }
                }
                string fallback = cb.Type?.Name ?? "";
                return (targetHint.Length > 0 ? $"{fallback} → {targetHint}" : fallback, "");
            }

            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return (cb.Type?.Name ?? "", "");
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            return ($"{typePart}{m.Name}", Path.GetFileName(m.Type?.Module?.Name ?? ""));
        }
        catch { return ("", ""); }
    }

    private static long ReadTimerLong(ClrObject obj, params string[] fieldNames)
    {
        if (!obj.IsValid) return -1;
        foreach (var field in fieldNames)
        {
            if (obj.Type?.GetFieldByName(field) is null) continue;
            try { return obj.ReadField<long>(field); } catch { }
            try { return obj.ReadField<uint>(field); } catch { }
            try { return obj.ReadField<int>(field); }  catch { }
        }
        return -1;
    }
}
