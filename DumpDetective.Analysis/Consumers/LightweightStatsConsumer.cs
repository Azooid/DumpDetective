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
/// Used when <c>DumpCollector.CollectLightweight</c> is called — e.g. for
/// trend-analysis snapshots where the detailed sub-reports will not be rendered.
/// The event-leak detection counts the subscriber list length for each delegate
/// field (walking the <c>_invocationList</c> chain if present) so the snapshot
/// records total subscription counts without storing individual subscriber objects.
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
        // Timer objects — just count; full detail is not needed for lightweight snapshots.
        if (meta.IsTimer)   { TimerCount++; return; }

        if (meta.IsWcf)
        {
            WcfCount++;
            // CommunicationState.Faulted == 5 — check the backing field name variants
            // used across different WCF / CoreWCF assemblies.
            if (TryReadIntField(obj, "_state", "_communicationState") == 5)
                WcfFaulted++;
        }

        if (meta.IsConnection) ConnCount++;

        // Walk each delegate-typed field to count subscribers.
        // This is the lightweight equivalent of EventDetailConsumer — it accumulates
        // total counts per (publisher-type, field-name) without collecting individual
        // subscriber details, keeping memory usage minimal.
        foreach (var field in meta.DelegateFields)
        {
            try
            {
                var delVal = field.Field.ReadObject(obj.Address, false);
                if (delVal.IsNull || !delVal.IsValid) continue;
                int subs = CountSubscribers(delVal); // counts _invocationList entries
                if (subs <= 0) continue;
                (string Publisher, string Field) key = (meta.Name, field.Name);
                ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault(EventLeakTotals, key, out _);
                existing += subs;
            }
            catch { }
        }
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new LightweightStatsConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (LightweightStatsConsumer)other;
        TimerCount += src.TimerCount;
        WcfCount   += src.WcfCount;
        WcfFaulted += src.WcfFaulted;
        ConnCount  += src.ConnCount;
        foreach (var (key, count) in src.EventLeakTotals)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(EventLeakTotals, key, out _);
            dst += count;
        }
    }

    private static int CountSubscribers(ClrObject del) => HeapWalker.CountSubscribers(del);

    private static int TryReadIntField(ClrObject obj, params string[] names)
    {
        foreach (var n in names) try { return obj.ReadField<int>(n); } catch { }
        return -1;
    }
}
