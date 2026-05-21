using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Memory.Consumers;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Detects unbounded cache-like collections (Dictionary, ConcurrentDictionary, MemoryCache, etc.)
/// by walking the heap, reading entry-count fields, and flagging instances whose count
/// exceeds a configurable threshold.
/// </summary>
public sealed class CachePatternsAnalyzer
{
    private static readonly string[] CountFieldNames = ["_count", "m_count", "_size", "count", "_entryCount"];

    // Entry-count reading and matching logic delegated to CachePatternMatcher.

    public CachePatternsData Analyze(DumpContext ctx, int unboundedThreshold = 10_000, int top = 30)
    {
        if (!ctx.Heap.CanWalkHeap)
            return new CachePatternsData([], 0, 0, 0);

        // Fast path: data already collected during main heap walk.
        if (ctx.GetAnalysis<CachePatternsConsumerResult>() is { } cached)
            return BuildFromCache(cached, ctx, unboundedThreshold, top);

        // Slow path: standalone invocation.
        var byType  = new Dictionary<string, MutableEntry>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);

        CommandBase.RunStatus("Scanning cache patterns...", update =>
        {
            long scanned = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                string typeName = obj.Type.Name ?? string.Empty;

                string? kind = CachePatternMatcher.Classify(typeName);
                if (kind is null) continue;

                scanned++;
                if ((scanned & 0xFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning caches \u2014 {scanned:N0} found  \u2022  {byType.Count:N0} types...");
                    sw.Restart();
                }

                long count = ReadEntryCount(obj);
                long size  = (long)obj.Size;

                if (!byType.TryGetValue(typeName, out var e))
                    byType[typeName] = e = new MutableEntry(kind);

                e.InstanceCount++;
                e.TotalSize    += size;
                e.TotalEntries += count;
                if (count > e.MaxEntries) e.MaxEntries = count;
                if (count > unboundedThreshold) e.HasOversized = true;

                if (!samples.TryGetValue(typeName, out var addrs))
                    samples[typeName] = addrs = [];
                if (addrs.Count < 5) addrs.Add(obj.Address);
            }
        });

        // BFS retained-size pass (optional — skipped when BFS index not yet built).
        BfsIndexCache? bfsCache = null;
        if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(ctx.DumpPath), ctx.DumpPath))
        {
            CommandBase.RunStatus("Loading BFS index for cache analysis...", update =>
                bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                    new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache);
        }

        var visited = new HashSet<int>();
        long nodeCap = bfsCache is not null ? Math.Min(500_000L, bfsCache.NodeCount) : 0;

        var entries = byType
            .Where(kv => kv.Value.InstanceCount > 0)
            .OrderByDescending(kv => kv.Value.TotalEntries)
            .Take(top)
            .Select(kv =>
            {
                var e = kv.Value;
                long avg = e.InstanceCount > 0 ? e.TotalEntries / e.InstanceCount : 0;
                long retained = 0; bool isEst = false;
                if (bfsCache is not null && samples.TryGetValue(kv.Key, out var addrs) && addrs.Count > 0)
                    retained = ComputeGroupRetained(bfsCache, addrs, e.InstanceCount, visited, nodeCap, out isEst);
                return new CachePatternEntry(kv.Key, e.InstanceCount, e.TotalEntries,
                    e.TotalSize, avg, e.MaxEntries, e.HasOversized, e.Kind, retained, isEst);
            })
            .ToList();

        return new CachePatternsData(
            entries,
            byType.Values.Sum(e => e.InstanceCount),
            byType.Values.Sum(e => e.TotalEntries),
            byType.Values.Sum(e => e.TotalSize));
    }

    private static long ReadEntryCount(in ClrObject obj) =>
        CachePatternMatcher.ReadEntryCount(obj);

    /// <summary>
    /// Samples up to 3 addresses from <paramref name="sampleAddrs"/>, computes BFS
    /// retained size for each, averages the result, then scales to
    /// <paramref name="instanceCount"/> to estimate total retained bytes for the group.
    /// </summary>
    private static long ComputeGroupRetained(
        BfsIndexCache bfsCache, IReadOnlyList<ulong> sampleAddrs,
        int instanceCount, HashSet<int> visited, long nodeCap, out bool isEst)
    {
        isEst = false;
        int n = Math.Min(sampleAddrs.Count, 3);
        if (n == 0) return 0;

        long sumRetained = 0;
        for (int i = 0; i < n; i++)
        {
            visited.Clear();
            var (ret, est) = bfsCache.ComputeRetained(sampleAddrs[i], visited, nodeCap);
            sumRetained += ret;
            if (est) isEst = true;
        }
        // Average retained per sampled instance, then scale to the full group.
        long avgRetained = sumRetained / n;
        if (n < instanceCount) isEst = true; // scaled estimate
        return avgRetained * instanceCount;
    }

    private sealed class MutableEntry(string kind)
    {
        public string Kind         = kind;
        public int    InstanceCount;
        public long   TotalEntries;
        public long   TotalSize;
        public long   MaxEntries;
        public bool   HasOversized;
    }

    private static CachePatternsData BuildFromCache(
        CachePatternsConsumerResult r, DumpContext ctx, int unboundedThreshold, int top)
    {
        // Load BFS index if available.
        BfsIndexCache? bfsCache = null;
        if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(ctx.DumpPath), ctx.DumpPath))
        {
            CommandBase.RunStatus("Loading BFS index for cache analysis...", update =>
                bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                    new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache);
        }

        var visited = new HashSet<int>();
        long nodeCap = bfsCache is not null ? Math.Min(500_000L, bfsCache.NodeCount) : 0;
        var entries = r.ByType
            .Where(kv => kv.Value.InstanceCount > 0)
            .OrderByDescending(kv => kv.Value.TotalEntries)
            .Take(top)
            .Select(kv =>
            {
                var e = kv.Value;
                long avg = e.InstanceCount > 0 ? e.TotalEntries / e.InstanceCount : 0;
                bool hasOversized = e.MaxEntries > unboundedThreshold;
                long retained = 0; bool isEst = false;
                if (bfsCache is not null && e.SampleAddrs.Count > 0)
                    retained = ComputeGroupRetained(bfsCache, e.SampleAddrs, e.InstanceCount, visited, nodeCap, out isEst);
                return new CachePatternEntry(kv.Key, e.InstanceCount, e.TotalEntries,
                    e.TotalSize, avg, e.MaxEntries, hasOversized, e.Kind, retained, isEst);
            })
            .ToList();

        return new CachePatternsData(
            entries,
            r.ByType.Values.Sum(e => e.InstanceCount),
            r.ByType.Values.Sum(e => e.TotalEntries),
            r.ByType.Values.Sum(e => e.TotalSize));
    }
}
