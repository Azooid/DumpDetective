using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Analysis;

public static class DumpCollector
{

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Full collection (string dups + event leaks) using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectFull(DumpContext ctx, Action<string>? progress = null)
        => CollectFromContext(ctx, full: true, progress);

    /// <summary>Lightweight collection using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectLightweight(DumpContext ctx, Action<string>? progress = null)
        => CollectFromContext(ctx, full: false, progress);

    /// <summary>Full collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectFull(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: true, progress);

    /// <summary>Lightweight collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectLightweight(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: false, progress);

    // ── Private collect paths ─────────────────────────────────────────────────

    private static DumpSnapshot CollectFromContext(DumpContext ctx, bool full, Action<string>? progress = null)
    {
        var snapshot = CreateSnapshot(ctx.DumpPath, ctx.FileTime, full);
        snapshot.ClrVersion = ctx.ClrVersion;
        CollectAll(ctx.Runtime, snapshot, full, progress, ctx);
        var (findings, score) = HealthScorer.Score(snapshot, ThresholdLoader.Current.Scoring);
        snapshot.Findings    = findings.ToList();
        snapshot.HealthScore = score;
        return snapshot;
    }

    private static DumpSnapshot Collect(string dumpPath, bool full, Action<string>? progress = null)
    {
        var snapshot = CreateSnapshot(
            dumpPath,
            File.Exists(dumpPath) ? File.GetLastWriteTime(dumpPath) : DateTime.UtcNow,
            full);

        var (runtime, dataTarget) = DumpHelpers.OpenDump(dumpPath);
        using var _dt = dataTarget;
        using var _rt = runtime;

        if (runtime is null) return snapshot;

        snapshot.ClrVersion = runtime.ClrInfo?.Version.ToString();
        return FinalizeSnapshot(runtime, snapshot, full, progress);
    }

    private static DumpSnapshot CreateSnapshot(string dumpPath, DateTime fileTime, bool full)
        => new()
        {
            DumpPath          = dumpPath,
            DumpFileSizeBytes = File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0,
            FileTime          = fileTime,
            IsFullMode        = full,
        };

    private static DumpSnapshot FinalizeSnapshot(ClrRuntime runtime, DumpSnapshot snapshot, bool full, Action<string>? progress)
    {
        CollectAll(runtime, snapshot, full, progress);
        var (findings, score) = HealthScorer.Score(snapshot, ThresholdLoader.Current.Scoring);
        snapshot.Findings    = findings.ToList();
        snapshot.HealthScore = score;
        return snapshot;
    }

    /// <summary>
    /// Core collection logic. When <paramref name="ctx"/> is provided and
    /// <paramref name="full"/> is <see langword="true"/>, a single combined heap walk
    /// builds both the <see cref="DumpSnapshot"/> fields and the cached
    /// <see cref="HeapSnapshot"/> in one pass, eliminating the second enumeration
    /// that <c>EnsureSnapshot</c> would otherwise trigger.
    /// </summary>
    private static void CollectAll(ClrRuntime runtime, DumpSnapshot snapshot, bool full,
                                   Action<string>? progress = null, DumpContext? ctx = null)
    {
        // Track elapsed time per sub-collector only when a progress listener is attached
        var sw = progress is not null ? Stopwatch.StartNew() : null;

        RuntimeSubCollectors.CollectThreads(runtime, snapshot);
        RuntimeSubCollectors.CollectThreadPool(runtime, snapshot);
        if (sw is not null)
        {
            progress!($"[SCAN]Thread scan|{snapshot.ThreadCount}|{sw.ElapsedMilliseconds}");
            sw.Restart();
        }

        RuntimeSubCollectors.CollectHandles(runtime, snapshot, progress);
        if (sw is not null)
        {
            progress!($"[SCAN]Handle scan|{snapshot.TotalHandleCount}|{sw.ElapsedMilliseconds}");
            sw.Restart();
        }

        RuntimeSubCollectors.CollectModules(runtime, snapshot);
        if (sw is not null)
        {
            progress!($"[SCAN]Module scan|{snapshot.ModuleCount}|{sw.ElapsedMilliseconds}");
        }

        var heap = runtime.Heap;
        if (heap.CanWalkHeap)
        {
            RuntimeSubCollectors.CollectSegmentLayout(heap, snapshot);
            if (ctx is not null && full)
                HeapObjectCollector.CollectHeapObjectsCombined(ctx, snapshot, progress);
            else
                HeapObjectCollector.CollectHeapObjects(heap, snapshot, full, progress);

            if (sw is not null) sw.Restart();
            RuntimeSubCollectors.CollectFinalizerQueue(heap, snapshot, progress);
            if (sw is not null)
                progress!($"[SCAN]Finalizer queue scan|{snapshot.FinalizerQueueDepth}|{sw.ElapsedMilliseconds}");
        }
    }
}

