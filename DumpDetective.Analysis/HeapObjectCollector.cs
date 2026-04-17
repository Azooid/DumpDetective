using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Analyzers;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis;

/// <summary>
/// Handles heap-object enumeration for <see cref="DumpCollector"/>.
/// Two code paths:
/// <list type="bullet">
///   <item><see cref="CollectHeapObjectsCombined"/> — full mode with a <see cref="DumpContext"/>:
///   single walk fills both <see cref="DumpSnapshot"/> and pre-populates
///   the <see cref="HeapSnapshot"/> cache so per-command analyzers get a
///   free cache hit later.</item>
///   <item><see cref="CollectHeapObjects"/> — lightweight or no-context mode:
///   fills only <see cref="DumpSnapshot"/>.</item>
/// </list>
/// </summary>
internal static class HeapObjectCollector
{
    // ── Combined walk (full mode + DumpContext) ───────────────────────────────
    // Single EnumerateObjects pass that fills DumpSnapshot AND pre-populates
    // ctx.Snapshot (HeapSnapshot), so RenderEmbeddedReports gets a cache hit
    // and never walks the heap a second time.

    internal static void CollectHeapObjectsCombined(DumpContext ctx, DumpSnapshot s, Action<string>? progress = null)
    {
        var heap = ctx.Heap;

        long committed = 0;
        foreach (var seg in heap.Segments)
            committed += (long)seg.CommittedMemory.Length;

        // ── Instantiate consumers ─────────────────────────────────────────────
        var typeStatsC = new Consumers.TypeStatsConsumer();
        var genCounter = new Consumers.GenCounterConsumer();
        var inbound    = new Consumers.InboundRefConsumer();
        var strings    = new Consumers.StringGroupConsumer();

        var timerAnalyzer = new Analyzers.TimerLeaksAnalyzer();       timerAnalyzer.Reset(ctx.Runtime);
        var wcfAnalyzer   = new Analyzers.WcfChannelsAnalyzer();       wcfAnalyzer.Reset();
        var connAnalyzer  = new Analyzers.ConnectionPoolAnalyzer();    connAnalyzer.Reset();
        var exAnalyzer    = new Analyzers.ExceptionAnalysisAnalyzer(); exAnalyzer.Reset();
        var asyncAnalyzer = new Analyzers.AsyncStacksAnalyzer();       asyncAnalyzer.Reset();
        var eventAnalyzer = new Analyzers.EventAnalysisAnalyzer();     eventAnalyzer.Reset();

        // ── Single heap walk — all consumers driven in one pass ───────────────
        long freeBytes = HeapWalker.Walk(heap,
            [typeStatsC, genCounter, inbound, strings,
             timerAnalyzer, wcfAnalyzer, connAnalyzer, exAnalyzer, asyncAnalyzer, eventAnalyzer],
            progress);

        // ── Populate DumpSnapshot from consumer results ───────────────────────
        s.FragmentationPct = committed > 0 ? freeBytes * 100.0 / committed : 0;
        s.HeapFreeBytes    = freeBytes;
        s.LohObjectCount   = genCounter.LohThresholdObjectCount;
        s.LohLiveBytes     = genCounter.LohThresholdLiveBytes;
        s.TotalObjectCount = typeStatsC.TotalObjects;
        s.StringTotalBytes = strings.TotalStringSize;
        s.TimerCount       = timerAnalyzer.Result!.Timers.Count;
        s.WcfObjectCount   = wcfAnalyzer.Result!.Objects.Count;
        s.WcfFaultedCount  = wcfAnalyzer.Result!.Objects.Count(static o => o.State == "Faulted");
        s.ConnectionCount  = connAnalyzer.Result!.Connections.Count;

        SnapshotPopulator.ApplyTopTypes(s, typeStatsC.TypeStats);
        SnapshotPopulator.ApplyExceptionCounts(s, exAnalyzer.Result!.Totals);

        var asyncMethodCounts = new Dictionary<string, int>(512, StringComparer.Ordinal);
        foreach (var entry in asyncAnalyzer.Result!.Entries)
        {
            ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(asyncMethodCounts, entry.Method, out bool existed);
            c = existed ? c + 1 : 1;
        }
        SnapshotPopulator.ApplyAsyncMethods(s, asyncMethodCounts, asyncAnalyzer.Result!.BacklogTotal);

        ctx.SetAnalysis(timerAnalyzer.Result!);
        ctx.SetAnalysis(wcfAnalyzer.Result!);
        ctx.SetAnalysis(connAnalyzer.Result!);
        ctx.SetAnalysis(exAnalyzer.Result!);
        ctx.SetAnalysis(asyncAnalyzer.Result!);
        ctx.SetAnalysis(eventAnalyzer.Result!);

        // ── String duplicate stats ────────────────────────────────────────────
        SnapshotPopulator.ApplyStringDuplicates(s, strings.StringGroups);

        // ── Event leak stats ──────────────────────────────────────────────────
        SnapshotPopulator.ApplyEventLeaks(s, eventAnalyzer.Result!.Groups);

        // ── Pre-populate HeapSnapshot so EnsureSnapshot() is a no-op later ───
        ctx.PreloadSnapshot(HeapSnapshot.Create(
            typeStatsC.TypeStats, inbound.InboundCounts, strings.StringGroups,
            genCounter.Gen0Bytes, genCounter.Gen1Bytes, genCounter.Gen2Bytes,
            genCounter.LohBytes,  genCounter.PohBytes,
            genCounter.Gen0ObjCount, genCounter.Gen1ObjCount, genCounter.Gen2ObjCount,
            genCounter.FrozenObjCount, genCounter.FrozenObjSize,
            genCounter.PohObjCount, genCounter.PohObjSize,
            typeStatsC.TotalObjects, inbound.TotalRefs,
            strings.TotalStringCount, strings.TotalStringSize));
    }

    // ── Main heap object walk ─────────────────────────────────────────────────

    internal static void CollectHeapObjects(ClrHeap heap, DumpSnapshot s, bool full, Action<string>? progress = null)
    {
        long committed = 0;
        foreach (var seg in heap.Segments)
            committed += (long)seg.CommittedMemory.Length;

        // ── Instantiate consumers ─────────────────────────────────────────────
        var typeStatsC = new Consumers.TypeStatsConsumer();
        var genCounter = new Consumers.GenCounterConsumer();
        var exConsumer = new Consumers.ExceptionCountConsumer();
        var asyncC     = new Consumers.AsyncMethodConsumer();
        var lwStats    = new Consumers.LightweightStatsConsumer();

        // Full mode only
        Consumers.StringGroupConsumer? strings = full ? new Consumers.StringGroupConsumer() : null;

        IReadOnlyList<IHeapObjectConsumer> consumers = full && strings is not null
            ? [typeStatsC, genCounter, exConsumer, asyncC, lwStats, strings]
            : [typeStatsC, genCounter, exConsumer, asyncC, lwStats];

        // ── Single heap walk ──────────────────────────────────────────────────
        long freeBytes = HeapWalker.Walk(heap, consumers, progress);

        // ── Populate DumpSnapshot ─────────────────────────────────────────────
        s.FragmentationPct = committed > 0 ? freeBytes * 100.0 / committed : 0;
        s.HeapFreeBytes    = freeBytes;
        s.LohObjectCount   = genCounter.LohThresholdObjectCount;
        s.LohLiveBytes     = genCounter.LohThresholdLiveBytes;
        s.TotalObjectCount = typeStatsC.TotalObjects;
        s.TimerCount       = lwStats.TimerCount;
        s.WcfObjectCount   = lwStats.WcfCount;
        s.WcfFaultedCount  = lwStats.WcfFaulted;
        s.ConnectionCount  = lwStats.ConnCount;

        SnapshotPopulator.ApplyTopTypes(s, typeStatsC.TypeStats);
        SnapshotPopulator.ApplyExceptionCounts(s, exConsumer.Totals);
        SnapshotPopulator.ApplyAsyncMethods(s, asyncC.MethodCounts, asyncC.BacklogTotal);
        SnapshotPopulator.ApplyEventLeaks(s, lwStats.EventLeakTotals);

        if (full && strings is not null)
        {
            s.StringTotalBytes = strings.TotalStringSize;
            SnapshotPopulator.ApplyStringDuplicates(s, strings.StringGroups);
        }
    }

}
