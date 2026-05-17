using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Memory.Analyzers;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Memory;

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

    internal static void CollectHeapObjectsCombined(DumpContext ctx, DumpSnapshot s, Action<string>? progress = null,
                                                    IReadOnlyList<IHeapObjectConsumer>? extraConsumers = null)
    {
        var heap = ctx.Heap;

        long committed = 0;
        foreach (var seg in heap.Segments)
            committed += (long)seg.CommittedMemory.Length;

        // ── Instantiate consumers ─────────────────────────────────────────────
        var typeStatsC  = new Consumers.TypeStatsConsumer();
        var genCounter  = new Consumers.GenCounterConsumer();
        var inbound     = new Consumers.InboundRefConsumer();
        var strings     = new Consumers.StringGroupConsumer();
        var threadNames = new Consumers.ThreadNameConsumer();
        var threadPool  = new Consumers.ThreadPoolConsumer();
        var httpReqs    = new Consumers.HttpRequestsConsumer();
        var cwt         = new Consumers.ConditionalWeakTableConsumer();

        var timerAnalyzer = new Analyzers.TimerLeaksAnalyzer();       timerAnalyzer.Reset(ctx.Runtime);
        var wcfAnalyzer   = new Analyzers.WcfChannelsAnalyzer();       wcfAnalyzer.Reset();
        var connAnalyzer  = new Analyzers.ConnectionPoolAnalyzer();    connAnalyzer.Reset();
        var exAnalyzer    = new Analyzers.ExceptionAnalysisAnalyzer(); exAnalyzer.Reset();
        var asyncAnalyzer = new Analyzers.AsyncStacksAnalyzer();       asyncAnalyzer.Reset();
        var eventAnalyzer = new Analyzers.EventAnalysisAnalyzer();     eventAnalyzer.Reset();

        // ── Secondary-metric consumers ────────────────────────────────────────
        // These are low-overhead consumers that collect data for commands that
        // previously did independent heap walks during the sub-reports phase.
        // Adding them here eliminates 4–5 parallel heap walks (each ~120s on large dumps).
        var dataTable     = new Consumers.DataTableConsumer();
        var cachePatterns = new Consumers.CachePatternsConsumer();
        var closures      = new Consumers.ClosureCaptureConsumer();
        var alc           = new Consumers.AlcConsumer();
        var largeObjects  = new Consumers.LargeObjectsConsumer(minSize: 85_000);

        // ── Single heap walk — all consumers driven in one pass ───────────────
        IReadOnlyList<IHeapObjectConsumer> allConsumers;
        if (extraConsumers is null || extraConsumers.Count == 0)
        {
            allConsumers = [typeStatsC, genCounter, inbound, strings,
                            threadNames, threadPool, httpReqs, cwt,
                            timerAnalyzer, wcfAnalyzer, connAnalyzer, exAnalyzer, asyncAnalyzer, eventAnalyzer,
                            dataTable, cachePatterns, closures, alc, largeObjects];
        }
        else
        {
            var list = new List<IHeapObjectConsumer>(19 + extraConsumers.Count)
            {
                typeStatsC, genCounter, inbound, strings,
                threadNames, threadPool, httpReqs, cwt,
                timerAnalyzer, wcfAnalyzer, connAnalyzer, exAnalyzer, asyncAnalyzer, eventAnalyzer,
                dataTable, cachePatterns, closures, alc, largeObjects
            };
            list.AddRange(extraConsumers);
            allConsumers = list;
        }
        long freeBytes = HeapWalker.Walk(heap, allConsumers, progress);

        // ── Populate DumpSnapshot from consumer results ───────────────────────
        s.FragmentationPct = committed > 0 ? freeBytes * 100.0 / committed : 0;
        s.HeapFreeBytes    = freeBytes;
        s.LohObjectCount   = genCounter.LohThresholdObjectCount;
        s.LohLiveBytes     = genCounter.LohBytes;     // live bytes from LOH segments only (LohThresholdLiveBytes includes large POH objects)
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
        // Note: EventAnalysisData is NOT pre-cached here because the consumer-based quick-count
        // lacks per-subscriber detail. EventAnalysisAnalyzer.Analyze() will do the full scan.

        // ── New full-mode consumers ────────────────────────────────────────────
        // ThreadNameMap: pre-seed into the once-cache so GetOrCreateAnalysis in
        // ThreadAnalysisAnalyzer/DeadlockAnalyzer returns instantly (~0 ms).
        ctx.PreloadAnalysis(threadNames.Map);
        ctx.SetAnalysis(httpReqs.Result!);
        ctx.SetAnalysis(new Consumers.CwtData(cwt.Entries));
        // ThreadPoolConsumer: results stored in threadPool.TaskCounts + threadPool.WorkItems;
        // ThreadPoolAnalyzer reads them via GetAnalysis<ThreadPoolConsumerCache>.
        ctx.SetAnalysis(new Consumers.ThreadPoolConsumerCache(threadPool.TaskCounts, threadPool.WorkItems));

        // ── String duplicate stats ────────────────────────────────────────────
        SnapshotPopulator.ApplyStringDuplicates(s, strings.StringGroups);

        // ── Event leak stats ──────────────────────────────────────────────────
        SnapshotPopulator.ApplyEventLeaks(s, eventAnalyzer.Result!.Groups);

        // ── Secondary-metric consumer results ─────────────────────────────────
        // Stored so analyzers get a free cache hit instead of re-walking the heap.
        ctx.SetAnalysis(new Consumers.DataTableConsumerResult(
            dataTable.DataTableCount, dataTable.DataRowCount, dataTable.DataColumnCount,
            dataTable.DataViewCount,  dataTable.DataSetCount,
            dataTable.TotalBytes,     dataTable.TopTables));
        ctx.SetAnalysis(new Consumers.CachePatternsConsumerResult(cachePatterns.ByType));
        ctx.SetAnalysis(new Consumers.ClosureCaptureConsumerResult(closures.ByType));
        ctx.SetAnalysis(new Consumers.AlcConsumerResult(alc.Entries));
        ctx.SetAnalysis(new Consumers.LargeObjectsConsumerResult(largeObjects.Objects));

        // ── Pre-populate HeapSnapshot so EnsureSnapshot() is a no-op later ───
        ctx.PreloadSnapshot(HeapSnapshot.Create(
            typeStatsC.TypeStats, inbound.InboundCounts, strings.StringGroups,
            genCounter.Gen0Bytes, genCounter.Gen1Bytes, genCounter.Gen2Bytes,
            genCounter.LohBytes,  genCounter.PohBytes,
            genCounter.Gen0ObjCount, genCounter.Gen1ObjCount, genCounter.Gen2ObjCount,
            genCounter.FrozenObjCount, genCounter.FrozenObjSize,
            genCounter.PohObjCount, genCounter.PohObjSize,
            typeStatsC.TotalObjects, inbound.TotalRefs,
            strings.TotalStringCount, strings.TotalStringSize,
            inbound.TopAddrs, inbound.Histogram, inbound.InboundCountsSize,
            dumpPath: ctx.DumpPath));

        // The three large dictionaries (TypeStats, InboundCounts, StringGroups) have been
        // written to temp files by HeapSnapshot.Create above. Release the consumer-side
        // references now so the GC can reclaim those backing arrays without waiting for
        // the next analysis phase to run.
        inbound.ReleaseRaw();
    }

    // ── Cache-build-only walk ─────────────────────────────────────────────────
    // Runs ONLY the four consumers that are strictly required to build ctx.Snapshot
    // (HeapSnapshot) — TypeStats, InboundRef, StringGroup, GenCounter — plus any
    // caller-supplied extras (e.g. FragmentationConsumer, BfsPass1Consumer).
    // All heavyweight analysis consumers (EventAnalysisAnalyzer, AsyncStacksAnalyzer,
    // WcfChannelsAnalyzer, etc.) are deliberately excluded to save ~30–50% walk time.
    // Use this from LoadCommand where the goal is writing cache files, not analyzing.

    internal static void CollectForCacheBuild(DumpContext ctx, IReadOnlyList<IHeapObjectConsumer> extraConsumers,
                                              Action<string>? progress = null,
                                              IReadOnlyList<IFinalizableObjectConsumer>? finalizableConsumers = null)
    {
        var heap = ctx.Heap;

        var typeStatsC = new Consumers.TypeStatsConsumer();
        var genCounter = new Consumers.GenCounterConsumer();
        var inbound    = new Consumers.InboundRefConsumer();
        var strings    = new Consumers.StringGroupConsumer();

        var allConsumers = new List<IHeapObjectConsumer>(4 + extraConsumers.Count)
            { typeStatsC, genCounter, inbound, strings };
        allConsumers.AddRange(extraConsumers);

        HeapWalker.Walk(heap, allConsumers, progress, finalizableConsumers);

        // Pre-populate HeapSnapshot — same as CollectHeapObjectsCombined, so
        // SharedReferrerCache.Build (and any other consumer of ctx.Snapshot) works normally.
        ctx.PreloadSnapshot(HeapSnapshot.Create(
            typeStatsC.TypeStats, inbound.InboundCounts, strings.StringGroups,
            genCounter.Gen0Bytes, genCounter.Gen1Bytes, genCounter.Gen2Bytes,
            genCounter.LohBytes,  genCounter.PohBytes,
            genCounter.Gen0ObjCount, genCounter.Gen1ObjCount, genCounter.Gen2ObjCount,
            genCounter.FrozenObjCount, genCounter.FrozenObjSize,
            genCounter.PohObjCount, genCounter.PohObjSize,
            typeStatsC.TotalObjects, inbound.TotalRefs,
            strings.TotalStringCount, strings.TotalStringSize,
            inbound.TopAddrs, inbound.Histogram, inbound.InboundCountsSize,
            dumpPath: ctx.DumpPath));

        inbound.ReleaseRaw();
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
        s.LohLiveBytes     = genCounter.LohBytes;     // live bytes from LOH segments only
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
