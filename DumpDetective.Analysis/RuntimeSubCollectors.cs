using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis;

/// <summary>
/// Non-heap sub-collectors called by <see cref="DumpCollector.CollectAll"/>.
/// Each method fills a distinct section of <see cref="DumpSnapshot"/>.
/// </summary>
internal static class RuntimeSubCollectors
{
    // ── Threads ───────────────────────────────────────────────────────────────

    internal static void CollectThreads(ClrRuntime runtime, DumpSnapshot s)
    {
        // Single pass — avoids ToList() allocation and 3 separate LINQ sweeps
        int total = 0, alive = 0, withEx = 0, blocked = 0;
        foreach (var t in runtime.Threads)
        {
            total++;
            if (t.IsAlive)                      alive++;
            if (t.CurrentException is not null) withEx++;

            // Check up to 5 stack frames for a known blocking call
            int frames = 0;
            foreach (var f in t.EnumerateStackTrace())
            {
                if (++frames > 5) break;
                var name = f.Method?.Name ?? string.Empty;
                if (name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                    || name.Contains("Wait", StringComparison.OrdinalIgnoreCase))
                {
                    blocked++;
                    break;  // count at most once per thread
                }
            }
        }
        s.ThreadCount          = total;
        s.AliveThreadCount     = alive;
        s.ExceptionThreadCount = withEx;
        s.BlockedThreadCount   = blocked;
    }

    // ── Thread pool ───────────────────────────────────────────────────────────

    internal static void CollectThreadPool(ClrRuntime runtime, DumpSnapshot s)
    {
        var tp = runtime.ThreadPool;
        if (tp is null) return;
        s.TpMinWorkers    = tp.MinThreads;
        s.TpMaxWorkers    = tp.MaxThreads;
        s.TpActiveWorkers = tp.ActiveWorkerThreads;
        s.TpIdleWorkers   = tp.IdleWorkerThreads;
    }

    // ── GC handles ────────────────────────────────────────────────────────────

    internal static void CollectHandles(ClrRuntime runtime, DumpSnapshot s, Action<string>? progress = null)
    {
        // Composite key (Kind, TypeName) groups handles by both kind and referenced type
        // so the report can show e.g. "Pinned: byte[] × 1 204" without string interpolation.
        var rootedByKey = new Dictionary<(ClrHandleKind Kind, string TypeName), (int Count, long Size)>(4096);
        var sw = progress is not null ? System.Diagnostics.Stopwatch.StartNew() : null;

        foreach (var h in runtime.EnumerateHandles())
        {
            // Tally every handle regardless of kind for the snapshot summary counters.
            s.TotalHandleCount++;
            if (h.IsPinned)  s.PinnedHandleCount++;
            if (h.IsStrong)  s.StrongHandleCount++;
            if (h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
                s.WeakHandleCount++;

            // Only strong handles keep objects alive — resolve the heap object for those.
            if (h.IsStrong)
            {
                try
                {
                    var obj = h.Object;
                    if (obj == 0) continue;
                    var heapObj = runtime.Heap.GetObject(obj);
                    if (!heapObj.IsValid) continue;
                    var typeName = heapObj.Type?.Name ?? "<unknown>";
                    var key      = (h.HandleKind, typeName);
                    long size    = (long)heapObj.Size;
                    // Tuple value type — use ref to update count/size in place without a copy.
                    ref var e = ref CollectionsMarshal.GetValueRefOrAddDefault(rootedByKey, key, out bool exists);
                    e = exists ? (e.Count + 1, e.Size + size) : (1, size);
                }
                catch { } // object may be partially collected
            }

            // Emit live spinner update at most every 200 ms to avoid console I/O overhead.
            if (progress is not null && sw!.ElapsedMilliseconds >= 200)
            {
                progress($"Scanning handles \u2014 {s.TotalHandleCount:N0} handles  \u2022  {s.StrongHandleCount:N0} strong  \u2022  {s.PinnedHandleCount:N0} pinned...");
                sw.Restart();
            }
        }

        // Keep only the top 15 rooted types by count for the snapshot summary.
        s.TopRootedTypes = rootedByKey
            .OrderByDescending(kv => kv.Value.Count)
            .Take(15)
            .Select(kv => new RootedHandleStat(kv.Key.Kind.ToString(), kv.Key.TypeName, kv.Value.Count, kv.Value.Size))
            .ToList();
    }

    // ── Modules ───────────────────────────────────────────────────────────────

    internal static void CollectModules(ClrRuntime runtime, DumpSnapshot s)
    {
        foreach (var m in runtime.EnumerateModules())
        {
            s.ModuleCount++;
            var path = m.Name ?? m.AssemblyName ?? string.Empty;
            if (!IsSystemAssemblyPath(path)) s.AppModuleCount++;
        }
    }

    // ── Segment layout (gen totals + fragmentation estimate) ─────────────────

    internal static void CollectSegmentLayout(ClrHeap heap, DumpSnapshot s)
    {
        foreach (var seg in heap.Segments)
        {
            long segCommitted = (long)seg.CommittedMemory.Length;

            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: s.Gen0Bytes   += segCommitted; break;
                case GCSegmentKind.Generation1: s.Gen1Bytes   += segCommitted; break;
                case GCSegmentKind.Generation2: s.Gen2Bytes   += segCommitted; break;
                case GCSegmentKind.Ephemeral:
                    s.Gen0Bytes += (long)seg.Generation0.Length;
                    s.Gen1Bytes += (long)seg.Generation1.Length;
                    s.Gen2Bytes += (long)seg.Generation2.Length;
                    break;
                case GCSegmentKind.Large:  s.LohBytes    += segCommitted; break;
                case GCSegmentKind.Pinned: s.PohBytes    += segCommitted; break;
                case GCSegmentKind.Frozen: s.FrozenBytes += segCommitted; break;
            }
        }

        s.TotalHeapBytes = s.Gen0Bytes + s.Gen1Bytes + s.Gen2Bytes
                         + s.LohBytes + s.PohBytes + s.FrozenBytes;
        // FragmentationPct is set in CollectHeapObjects to avoid a second full heap walk
    }

    // ── Finalizer queue ───────────────────────────────────────────────────────

    internal static void CollectFinalizerQueue(ClrHeap heap, DumpSnapshot s, Action<string>? progress = null)
    {
        var counts = new Dictionary<string, int>(256, StringComparer.Ordinal);
        int total = 0;

        var totalWatch = progress is not null ? System.Diagnostics.Stopwatch.StartNew() : null;
        var rateWatch  = progress is not null ? System.Diagnostics.Stopwatch.StartNew() : null;
        int lastCount  = 0;

        foreach (var obj in heap.EnumerateFinalizableObjects())
        {
            if (!obj.IsValid) continue;
            var name = obj.Type?.Name ?? "<unknown>";
            ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, name, out _);
            c++;
            total++;

            if (progress is not null && (total & 0x3FF) == 0 && rateWatch!.ElapsedMilliseconds >= 200)
            {
                double elapsed  = totalWatch!.Elapsed.TotalSeconds;
                double interval = rateWatch.Elapsed.TotalSeconds;
                int    delta    = total - lastCount;
                long   rate     = interval > 0 ? (long)(delta / interval) : 0;
                lastCount = total;
                rateWatch.Restart();
                progress($"Scanning finalizer queue — {total:N0} objs  •  {elapsed:F1}s  •  ~{rate:N0}/s");
            }
        }
        s.FinalizerQueueDepth = total;
        s.TopFinalizerTypes   = counts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new NameCount(kv.Key, kv.Value))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSystemAssemblyPath(string path) =>
        path.Contains("\\dotnet\\shared\\",      StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\windows\\assembly\\",   StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\gac_",                  StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("System.",    StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("mscorlib",   StringComparison.OrdinalIgnoreCase);
}
