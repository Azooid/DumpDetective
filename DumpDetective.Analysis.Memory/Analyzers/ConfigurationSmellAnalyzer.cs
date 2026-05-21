using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Analyzes observable runtime configuration from a memory dump and flags
/// likely misconfigurations: ThreadPool under-sizing, wrong GC mode,
/// GC heap count mismatch, finalizer starvation signals, etc.
///
/// All checks are heuristic — they detect patterns that are very likely
/// problems but cannot definitively confirm root cause without app config files.
/// </summary>
public sealed class ConfigurationSmellAnalyzer
{
    public ConfigSmellData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<ConfigSmellData>() is { } cached) return cached;

        var runtime  = ctx.Runtime;
        var threads  = runtime.Threads.ToList();
        int procCount = Environment.ProcessorCount;

        // ── Observable config ─────────────────────────────────────────────
        // Server GC detection heuristic: in server GC there is one ephemeral segment per
        // logical CPU; workstation GC has exactly 1. Count distinct ephemeral segments.
        int    heapCount   = runtime.Heap.Segments.Count(s =>
            s.Kind is GCSegmentKind.Ephemeral or GCSegmentKind.Generation0);
        bool   serverGc    = heapCount > 1;
        int    aliveCount  = threads.Count(t => t.IsAlive);
        int    blocked     = threads.Count(t => t.IsAlive && IsBlocked(t));
        string clrVersion  = ctx.ClrVersion ?? "(unknown)";
        bool   is64        = runtime.DataTarget?.DataReader?.PointerSize == 8;

        // ThreadPool values — available from ClrThreadPool when present
        int tpMinWorkers = 0, tpMaxWorkers = 0, tpMinIo = 0, tpMaxIo = 0;
        var tp = runtime.ThreadPool;
        if (tp is not null)
        {
            tpMinWorkers = tp.MinThreads;
            tpMaxWorkers = tp.MaxThreads;
        }

        var config = new ObservableRuntimeConfig(
            serverGc, heapCount, procCount, aliveCount,
            tpMinWorkers, tpMaxWorkers, tpMinIo, tpMaxIo,
            blocked, clrVersion, is64);

        // ── Smell detection ───────────────────────────────────────────────
        var smells = new List<ConfigSmell>();

        // 1. Server GC disabled for a heavily multi-threaded workload
        if (!serverGc && aliveCount > 20)
        {
            smells.Add(new ConfigSmell(
                ConfigSmellSeverity.Warning,
                "GC",
                "Server GC disabled with high thread count",
                $"Workstation GC is enabled but {aliveCount} threads are alive. " +
                "Workstation GC uses a single GC heap and pauses ALL threads for collection. " +
                "Server GC creates one heap per logical CPU and runs collections in parallel.",
                "Add to runtimeconfig.json: \"System.GC.Server\": true",
                70));
        }

        // 2. GC heap count doesn't match logical CPU count (server GC misconfiguration)
        if (serverGc && heapCount > 0 && heapCount < procCount / 2)
        {
            smells.Add(new ConfigSmell(
                ConfigSmellSeverity.Warning,
                "GC",
                "GC heap count significantly below CPU count",
                $"Server GC is enabled but only {heapCount} GC heaps are active vs {procCount} logical CPUs. " +
                "This can happen when GCHeapCount is explicitly lowered or when the process started " +
                "with processor affinity restrictions.",
                "Remove explicit GCHeapCount configuration or adjust processor affinity. " +
                "Check DOTNET_GCHeapCount environment variable.",
                55));
        }

        // 3. ThreadPool: min workers below CPU count (may cause initial ramp-up latency)
        if (tpMinWorkers > 0 && tpMinWorkers < procCount)
        {
            smells.Add(new ConfigSmell(
                ConfigSmellSeverity.Info,
                "ThreadPool",
                "ThreadPool minimum worker threads below CPU count",
                $"MinWorkerThreads = {tpMinWorkers}, logical CPUs = {procCount}. " +
                "The ThreadPool hill-climbing algorithm injects threads slowly if min is low, " +
                "causing latency spikes during burst traffic.",
                $"Call ThreadPool.SetMinThreads({procCount * 2}, {(tpMinIo > 0 ? tpMinIo : procCount)}) " +
                "at application startup for web workloads, or use DOTNET_ThreadPool_UnfairSemaphoreSpinLimit.",
                45));
        }

        // 4. Very high thread count (> 8× CPU) — indicates thread starvation or too many blocking calls
        int threadThreshold = procCount * 8;
        if (aliveCount > threadThreshold)
        {
            smells.Add(new ConfigSmell(
                ConfigSmellSeverity.Critical,
                "ThreadPool",
                "Extremely high thread count — likely starvation or blocking code",
                $"{aliveCount} alive threads vs {procCount} CPUs (threshold: {threadThreshold}). " +
                "The ThreadPool has injected many threads to compensate for blocked workers. " +
                "This is a classic sign of sync-over-async code (Task.Result / .Wait() calls) " +
                "or long-running blocking operations on ThreadPool threads.",
                "Search for .Result, .Wait(), GetAwaiter().GetResult() in hot paths. " +
                "Use async/await consistently. Consider ConfigureAwait(false) in libraries.",
                90));
        }

        // 5. High blocked-thread fraction (> 60% of alive threads blocked)
        if (aliveCount > 5 && blocked > 0)
        {
            double blockedPct = (double)blocked / aliveCount * 100;
            if (blockedPct >= 60)
            {
                smells.Add(new ConfigSmell(
                    ConfigSmellSeverity.Critical,
                    "ThreadPool",
                    "High fraction of threads are blocked",
                    $"{blocked} of {aliveCount} alive threads ({blockedPct:F0}%) are in a blocked state " +
                    "(WaitSleepJoin, Suspended, or WaitForActivation). " +
                    "This severely limits throughput and can cause request timeouts.",
                    "Identify the most common blocking call site via 'thread-analysis'. " +
                    "Convert synchronous blocking to async patterns or use dedicated threads for inherently blocking work.",
                    85));
            }
        }

        // 6. 32-bit process with large heap (> 512 MB)
        if (!is64)
        {
            try
            {
                long heapBytes = runtime.Heap.Segments.Sum(s => (long)(s.End - s.Start));
                if (heapBytes > 512L * 1024 * 1024)
                {
                    smells.Add(new ConfigSmell(
                        ConfigSmellSeverity.Warning,
                        "Process",
                        "Large heap in 32-bit process",
                        $"Heap size is ~{heapBytes / 1024 / 1024:N0} MB in a 32-bit process. " +
                        "32-bit processes have a 2-4 GB virtual address space limit. " +
                        "Near-limit operation causes OOM exceptions even when physical RAM is available.",
                        "Publish as 64-bit (AnyCPU / x64). " +
                        "Set <PlatformTarget>x64</PlatformTarget> in the project file.",
                        75));
                }
            }
            catch { /* segment enumeration may fail on corrupted dumps */ }
        }

        smells.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var result = new ConfigSmellData(smells, config, smells.Count);
        ctx.SetAnalysis(result);
        return result;
    }

    /// <summary>
    /// Heuristic: a thread is "blocked" if its top 5 frames contain a known blocking call.
    /// Mirrors the logic in <c>RuntimeSubCollectors.CollectThreads</c>.
    /// </summary>
    private static bool IsBlocked(ClrThread t)
    {
        int frames = 0;
        try
        {
            foreach (var f in t.EnumerateStackTrace())
            {
                if (++frames > 5) break;
                var name = f.Method?.Name ?? string.Empty;
                if (name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                    || name.Contains("Wait", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
