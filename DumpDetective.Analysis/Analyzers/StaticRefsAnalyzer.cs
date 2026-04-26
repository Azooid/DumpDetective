using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Enumerates all non-system static object-reference fields and computes retained sizes.
///
/// Two modes controlled by <c>bfsDepth</c>:
///   null (default in full-analyze / trend) — sampling mode: BFS walks up to
///     <see cref="DefaultSampleDepth"/> nodes per declaring type, then extrapolates the
///     full retained size from the average bytes/node seen so far. Fast and accurate
///     enough for health scoring and trend deltas.
///   0 — exact mode: BFS walks every reachable node with no cap. Accurate but slow
///     on large heaps (use for standalone <c>static-refs</c> runs where correctness matters).
///   N > 0 — custom sample depth: BFS walks up to N nodes then extrapolates.
///
/// All fields of the same declaring type share one visited set so shared sub-graphs
/// are counted once per type (e.g. LocalEntityCacheManager's 231 fields correctly
/// report the total retained bytes of the entire cache graph, not 231× it).
/// </summary>
public sealed class StaticRefsAnalyzer
{
    // Default sample depth = 1% of total heap objects, clamped to [10_000, 80% of total objects].
    // For a 1M object heap  →  10,000 nodes (minimum floor)
    // For a 10M object heap →  100,000 nodes
    // For a 110M object heap → 88,000,000 nodes (80% ceiling)
    public const int DefaultSampleDepth = -1; // sentinel: resolve at runtime from heap size
    private const int SampleRatioPercent = 1;
    private const int SampleMinNodes     = 10_000;
    private const double SampleMaxRatio  = 0.80; // ceiling = 80% of total heap objects
    private const int MaxParallelBfs     = 8;

    private static long ResolveSampleDepth(DumpContext ctx)
    {
        long totalObjs = ctx.Snapshot?.TotalObjects
            ?? ctx.Heap.Segments.Sum(s => (long)s.ObjectRange.Length / 24);
        long ceiling  = (long)(totalObjs * SampleMaxRatio);
        long computed = Math.Clamp(totalObjs * SampleRatioPercent / 100,
                                   SampleMinNodes, ceiling);
        return computed;
    }

    private static readonly HashSet<string> CollectionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dictionary", "List", "HashSet", "Queue", "Stack", "Array", "Cache", "ConcurrentDictionary",
        "ConcurrentBag", "ConcurrentQueue", "ImmutableDictionary", "ImmutableList",
    };

    /// <summary>
    /// Analyze static references.
    /// </summary>
    /// <param name="ctx">Dump context.</param>
    /// <param name="filter">Optional type/field name filter.</param>
    /// <param name="excludes">Optional type names to exclude.</param>
    /// <param name="bfsDepth">
    /// null = sampling mode (uses <see cref="DefaultSampleDepth"/>),
    /// 0    = exact mode (no cap, full BFS),
    /// N    = custom sample depth.
    /// </param>
    public StaticRefsData Analyze(DumpContext ctx, string? filter = null,
        HashSet<string>? excludes = null, long? bfsDepth = null)
    {
        // Resolve effective node cap.
        // null  = sampling mode — derive depth from heap object count (1% clamped)
        // 0     = exact mode (no cap) → long.MaxValue internally
        // N > 0 = custom sample depth
        long nodeCap     = bfsDepth.HasValue
            ? (bfsDepth.Value == 0 ? long.MaxValue : bfsDepth.Value)
            : ResolveSampleDepth(ctx);
        bool isExactMode = nodeCap == long.MaxValue;

        // Phase 1: group roots by declaring type.
        var byType = new Dictionary<string, List<(string FieldName, string FieldType, ulong Addr)>>(
            StringComparer.Ordinal);

        CommandBase.RunStatus("Scanning static fields + BFS retained sizes...", update =>
        {
            update("Enumerating static fields...");
            var staticRoots = ctx.GetOrCreateAnalysis<StaticRootEntries>(() => StaticRootEntries.Build(ctx));
            foreach (var entry in staticRoots.Entries)
            {
                if (DumpHelpers.IsSystemType(entry.DeclType)) continue;
                if (excludes is not null && excludes.Any(e =>
                    entry.DeclType.Contains(e, StringComparison.OrdinalIgnoreCase))) continue;
                if (filter is not null &&
                    !entry.DeclType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !entry.FieldName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!byType.TryGetValue(entry.DeclType, out var list))
                    byType[entry.DeclType] = list = [];
                list.Add((entry.FieldName, entry.FieldType, entry.Addr));
            }

            // Build MethodTable → typical object size cache from the snapshot.
            // This lets BfsWithSampling look up child sizes without calling
            // heap.GetObject(childAddr) — eliminating ~50% of dump I/O reads.
            // For variable-size types (arrays, strings) the cache holds the average
            // size seen during the heap walk, which is accurate enough for retained
            // size estimation.
            var mtSizeCache = new Dictionary<ulong, long>(4096);
            if (ctx.Snapshot is { } sizeSnap)
            {
                foreach (var (_, agg) in sizeSnap.TypeStats)
                    if (agg.MT != 0 && agg.Count > 0)
                        mtSizeCache[agg.MT] = agg.Size / agg.Count;
            }

            // Phase 2: BFS per declaring type with a shared visited set.
            var typeList = byType.ToList();
            var results  = new (string DeclType, List<StaticFieldEntry> Fields)[typeList.Count];
            long totalSz = 0;
            int  done    = 0;
            var  sw      = System.Diagnostics.Stopwatch.StartNew();

            string modeLabel = isExactMode ? "exact" : $"sampling({nodeCap:N0} nodes)";

            Parallel.ForEach(
                Enumerable.Range(0, typeList.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelBfs },
                i =>
                {
                    var (declType, fields) = typeList[i];
                    // Shared visited set across all fields of this declaring type.
                    var visited   = new HashSet<ulong>(256);
                    var entries   = new List<StaticFieldEntry>(fields.Count);

                    // Small per-worker miss cache — avoids copying the full shared MT-size map.
                    // Shared snapshot-derived entries remain read-only in mtSizeCache.
                    var localMtSizeMisses = new Dictionary<ulong, long>(64);

                    // Deduplicate identical root addresses within the same declaring type.
                    // If multiple fields point at the same object graph we only BFS it once.
                    // The first field row receives the retained size; duplicate rows get 0 so
                    // the per-type totals remain accurate when grouped in the report.
                    foreach (var group in fields.GroupBy(f => f.Addr))
                    {
                        var first = group.First();
                        var (fieldRet, wasEstimated) = BfsWithSampling(
                            ctx.Heap, group.Key, visited, nodeCap, mtSizeCache, localMtSizeMisses);

                        entries.Add(new StaticFieldEntry(
                            DeclType:     declType,
                            FieldName:    first.FieldName,
                            FieldType:    first.FieldType,
                            IsCollection: IsCollectionType(first.FieldType),
                            RetainedSize: fieldRet,
                            Addr:         group.Key,
                            IsEstimated:  wasEstimated));
                        Interlocked.Add(ref totalSz, fieldRet);

                        foreach (var dup in group.Skip(1))
                        {
                            entries.Add(new StaticFieldEntry(
                                DeclType:     declType,
                                FieldName:    dup.FieldName,
                                FieldType:    dup.FieldType,
                                IsCollection: IsCollectionType(dup.FieldType),
                                RetainedSize: 0,
                                Addr:         dup.Addr,
                                IsEstimated:  wasEstimated));
                        }
                    }

                    results[i] = (declType, entries);
                    int cur = Interlocked.Increment(ref done);
                    if (sw.ElapsedMilliseconds >= 200)
                    {
                        sw.Restart();
                        update($"BFS retained sizes [{modeLabel}] \u2014 {cur}/{typeList.Count} types  \u2022  {DumpHelpers.FormatSize(Interlocked.Read(ref totalSz))} retained...");
                    }
                });

            var allFields = new List<StaticFieldEntry>(results.Sum(r => r.Fields.Count));
            foreach (var (_, typeFields) in results) allFields.AddRange(typeFields);
            allFields.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));
            _fields     = allFields;
            _totalSz    = Interlocked.Read(ref totalSz);
            _isEstimated = !isExactMode;
        });

        var finalFields = _fields ?? [];
        return new StaticRefsData(finalFields, finalFields.Count, _totalSz, _isEstimated);
    }

    private List<StaticFieldEntry>? _fields;
    private long _totalSz;
    private bool _isEstimated;

    /// <summary>
    /// BFS from rootAddr using a shared visited set (shared across sibling fields).
    /// When nodeCap is reached the walk stops and the remaining size is extrapolated
    /// from the average bytes-per-node observed in the sample.
    ///
    /// Key optimisation: child sizes are looked up from <paramref name="mtSizeCache"/>
    /// (MethodTable → typical object size) built once from HeapSnapshot.TypeStats.
    /// This avoids calling heap.GetObject() on every child just to read its Size,
    /// halving the number of random I/O reads against the dump file.
    ///
    /// Returns (retainedBytes, wasEstimated).
    /// </summary>
    private static (long Size, bool Estimated) BfsWithSampling(
        ClrHeap heap, ulong rootAddr, HashSet<ulong> visited, long nodeCap,
        IReadOnlyDictionary<ulong, long> mtSizeCache,
        Dictionary<ulong, long> localMtSizeMisses)
    {
        if (rootAddr == 0 || !visited.Add(rootAddr)) return (0, false);
        var root = heap.GetObject(rootAddr);
        if (!root.IsValid || root.IsNull) return (0, false);

        long sampledSize  = (long)root.Size;
        int  sampledNodes = 1;
        var  stack        = new Stack<ulong>(64);
        stack.Push(rootAddr);

        while (stack.Count > 0)
        {
            if ((long)visited.Count >= nodeCap)
            {
                double avgBytesPerNode = sampledNodes > 0
                    ? (double)sampledSize / sampledNodes : 0;
                long extrapolated = (long)(stack.Count * avgBytesPerNode);
                return (sampledSize + extrapolated, true);
            }

            var obj = heap.GetObject(stack.Pop());
            if (!obj.IsValid || obj.IsNull) continue;

            try
            {
                foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (childAddr == 0 || !visited.Add(childAddr)) continue;

                    // Look up size from MT cache — avoids heap.GetObject(childAddr)
                    // which would read the object header from the dump file.
                    // Fall back to GetObject only when MT is unknown (rare for non-system types).
                    long childSize = 0;
                    var  childType = heap.GetObjectType(childAddr);
                    if (childType is not null)
                    {
                        if (!mtSizeCache.TryGetValue(childType.MethodTable, out childSize) &&
                            !localMtSizeMisses.TryGetValue(childType.MethodTable, out childSize))
                        {
                            // First time we see this MT — read once and cache.
                            var childObj = heap.GetObject(childAddr);
                            childSize = childObj.IsValid ? (long)childObj.Size : 0;
                            localMtSizeMisses[childType.MethodTable] = childSize;
                        }
                    }
                    else
                    {
                        var childObj = heap.GetObject(childAddr);
                        if (!childObj.IsValid || childObj.IsNull) continue;
                        childSize = (long)childObj.Size;
                    }

                    if (childSize == 0) continue;
                    sampledSize  += childSize;
                    sampledNodes++;
                    stack.Push(childAddr);
                    if ((long)visited.Count >= nodeCap) break;
                }
            }
            catch { }
        }

        return (sampledSize, false);
    }

    private static bool IsCollectionType(string typeName) =>
        CollectionMarkers.Any(m => typeName.Contains(m, StringComparison.OrdinalIgnoreCase));
}