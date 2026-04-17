using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Lightweight consumer used in the non-full (lightweight snapshot) walk.
/// Accumulates timer, WCF, connection, and event-leak counts without
/// building the full per-object result sets that the Analyzer classes produce.
/// </summary>
internal sealed class LightweightStatsConsumer : IHeapObjectConsumer
{
    public int TimerCount    { get; private set; }
    public int WcfCount      { get; private set; }
    public int WcfFaulted    { get; private set; }
    public int ConnCount     { get; private set; }

    public Dictionary<(string Publisher, string Field), int> EventLeakTotals { get; }
        = new(4096);

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.IsTimer)   { TimerCount++; return; }
        if (meta.IsWcf)
        {
            WcfCount++;
            if (TryReadIntField(obj, "_state", "_communicationState") == 5)
                WcfFaulted++;
        }
        if (meta.IsConnection) ConnCount++;

        foreach (var field in meta.DelegateFields)
        {
            try
            {
                var delVal = field.Field.ReadObject(obj.Address, false);
                if (delVal.IsNull || !delVal.IsValid) continue;
                int subs = CountSubscribers(delVal);
                if (subs <= 0) continue;
                (string Publisher, string Field) key = (meta.Name, field.Name);
                ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault(EventLeakTotals, key, out _);
                existing += subs;
            }
            catch { }
        }
    }

    public void OnWalkComplete() { }

    private static int CountSubscribers(ClrObject del) => HeapWalker.CountSubscribers(del);

    private static int TryReadIntField(ClrObject obj, params string[] names)
    {
        foreach (var n in names) try { return obj.ReadField<int>(n); } catch { }
        return -1;
    }
}
