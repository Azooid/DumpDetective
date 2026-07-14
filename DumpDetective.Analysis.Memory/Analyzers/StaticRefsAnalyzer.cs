using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Enumerates all non-system static object-reference fields and computes retained sizes.
///
/// Two modes controlled by <c>bfsDepth</c>:
///   null (default in full-analyze / trend / standalone) — exact mode: BFS walks every
///     reachable node with no cap.
///   0 — exact mode: BFS walks every reachable node with no cap.
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
        // Load BFS cache once — replaces ClrMD-based BfsWithSampling entirely when available.
        BfsIndexCache? bfsCache = null;
        if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(ctx.DumpPath), ctx.DumpPath))
        {
            CommandBase.RunStatus("Loading BFS index...", update =>
                bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                    new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath, update))).Cache);
        }

        // Resolve effective node cap (only relevant when cache is absent).
        // null  = exact mode (no cap)
        // 0     = exact mode (no cap) → long.MaxValue internally
        // N > 0 = custom sample depth
        long nodeCap     = bfsDepth.HasValue
            ? (bfsDepth.Value == 0 ? long.MaxValue : bfsDepth.Value)
            : long.MaxValue;
        bool isExactMode = nodeCap == long.MaxValue;

        // Phase 1: group roots by declaring type.
        var byType = new Dictionary<string, List<(string FieldName, string FieldType, ulong Addr)>>(
            StringComparer.Ordinal);

        CommandBase.RunStatus("Scanning static fields + BFS retained sizes...", update =>
        {
            update("Enumerating static fields...");
            var staticRoots = ctx.GetOrCreateAnalysis<StaticRootEntries>(() => StaticRootEntries.Build(ctx));
            _skippedModules = staticRoots.SkippedModules;
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

            // Build MethodTable → typical object size cache (only needed for BfsWithSampling).
            var mtSizeCache = new Dictionary<ulong, long>(4096);
            if (bfsCache is null && ctx.Snapshot is { } sizeSnap)
            {
                sizeSnap.RegisterTypeStatsReader();
                try
                {
                    foreach (var (_, agg) in sizeSnap.StreamTypeStats())
                        if (agg.MT != 0 && agg.Count > 0)
                            mtSizeCache[agg.MT] = agg.Size / agg.Count;
                }
                finally
                {
                    sizeSnap.RetireTypeStatsReader();
                }
            }

            // Phase 2: BFS per declaring type with a shared visited set.
            var typeList = byType.ToList();
            // Use a ConcurrentQueue so each parallel worker adds directly without holding
            // all per-type List<StaticFieldEntry> arrays alive simultaneously.
            // ConcurrentQueue is a lock-free linked-segment queue — no thread-local stealing
            // overhead like ConcurrentBag, and no per-call lock like a List+lock approach.
            var allFieldsQueue = new ConcurrentQueue<StaticFieldEntry>();
            long totalSz = 0;
            int  done    = 0;
            var  sw      = System.Diagnostics.Stopwatch.StartNew();

            string modeLabel = bfsCache is not null ? "bfs-cache"
                : isExactMode ? "exact" : $"sampling({nodeCap:N0} nodes)";

            Parallel.ForEach(
                Enumerable.Range(0, typeList.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelBfs },
                i =>
                {
                    var (declType, fields) = typeList[i];

                    // Deduplicate identical root addresses within the same declaring type.
                    // First field row gets retained size; duplicate rows get 0.
                    if (bfsCache is not null)
                    {
                        // Fast path: in-memory CSR graph — no ClrMD I/O.
                        var visitedIdx = new HashSet<int>(256);
                        foreach (var group in fields.GroupBy(f => f.Addr))
                        {
                            var first = group.First();
                            var (fieldRet, _) = bfsCache.ComputeRetained(group.Key, visitedIdx);
                            Interlocked.Add(ref totalSz, fieldRet);

                            allFieldsQueue.Enqueue(new StaticFieldEntry(
                                DeclType:     declType,
                                FieldName:    first.FieldName,
                                FieldType:    first.FieldType,
                                IsCollection: IsCollectionType(first.FieldType),
                                RetainedSize: fieldRet,
                                Addr:         group.Key,
                                IsEstimated:  false));

                            foreach (var dup in group.Skip(1))
                                allFieldsQueue.Enqueue(new StaticFieldEntry(declType, dup.FieldName, dup.FieldType,
                                    IsCollectionType(dup.FieldType), 0, dup.Addr, false));
                        }
                    }
                    else
                    {
                        // Slow path: BFS over ClrMD heap with optional sampling.
                        var visited = new HashSet<ulong>(256);
                        var localMtSizeMisses = new Dictionary<ulong, long>(64);
                        long workerNodesWalked = 0L;

                        foreach (var group in fields.GroupBy(f => f.Addr))
                        {
                            var first = group.First();
                            var (fieldRet, wasEstimated) = BfsWithSampling(
                                ctx.Heap, group.Key, visited, nodeCap, mtSizeCache, localMtSizeMisses,
                                ref workerNodesWalked, modeLabel, update);

                            allFieldsQueue.Enqueue(new StaticFieldEntry(
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
                                allFieldsQueue.Enqueue(new StaticFieldEntry(
                                    DeclType:     declType,
                                    FieldName:    dup.FieldName,
                                    FieldType:    dup.FieldType,
                                    IsCollection: IsCollectionType(dup.FieldType),
                                    RetainedSize: 0,
                                    Addr:         dup.Addr,
                                    IsEstimated:  wasEstimated));
                            }
                        }
                    }

                    int cur = Interlocked.Increment(ref done);
                    if (sw.ElapsedMilliseconds >= 200)
                    {
                        sw.Restart();
                        update($"BFS retained sizes [{modeLabel}] \u2014 {cur}/{typeList.Count} types  \u2022  {DumpHelpers.FormatSize(Interlocked.Read(ref totalSz))} retained...");
                    }
                });

            var allFields = allFieldsQueue.ToList(); // single allocation after all workers done
            allFields.Sort((a, b) => b.RetainedSize.CompareTo(a.RetainedSize));
            _fields     = allFields;
            _totalSz    = Interlocked.Read(ref totalSz);
            _isEstimated = bfsCache is null && !isExactMode;
        });

        var finalFields = _fields ?? [];

        IReadOnlyList<NonRefStaticFieldEntry>? nonRefFields = null;
        CommandBase.RunStatus("Scanning value-type static fields...", _ =>
            nonRefFields = CollectNonRefStaticFields(ctx, filter, excludes));

        return new StaticRefsData(finalFields, finalFields.Count, _totalSz, _isEstimated, _skippedModules, nonRefFields);
    }

    private List<StaticFieldEntry>? _fields;
    private long _totalSz;
    private bool _isEstimated;
    private int  _skippedModules;

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
        Dictionary<ulong, long> localMtSizeMisses,
        ref long sharedNodesWalked,
        string modeLabel,
        Action<string> update)
    {
        if (rootAddr == 0 || !visited.Add(rootAddr)) return (0, false);
        var root = heap.GetObject(rootAddr);
        if (!root.IsValid || root.IsNull) return (0, false);

        long sampledSize  = (long)root.Size;
        int  sampledNodes = 1;
        var  stack        = new Stack<ulong>(64);
        stack.Push(rootAddr);
        const int ProgressInterval = 250_000;
        long lastReport = 0;

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
                    // Variable-size types (string, array) cannot be cached by MT because
                    // each instance has a unique size — always read from the object directly.
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

                    if (childSize == 0) childSize = 0; // still recurse — child may reference large objects
                    sampledSize  += childSize;
                    if (childSize > 0) sampledNodes++;
                    stack.Push(childAddr);

                    long total = Interlocked.Increment(ref sharedNodesWalked);
                    if (total - lastReport >= ProgressInterval)
                    {
                        lastReport = total;
                        update($"BFS [{modeLabel}] \u2014 {total:N0} nodes  \u2022  {DumpHelpers.FormatSize(sampledSize)} retained so far...");
                    }

                    if ((long)visited.Count >= nodeCap) break;
                }
            }
            catch { }
        }

        return (sampledSize, false);
    }

    private static bool IsCollectionType(string typeName) =>
        CollectionMarkers.Any(m => typeName.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Enumerate all non-reference (value-type) static fields across non-system types.
    /// Reads primitive/enum values; leaves structs/pointers with a null value.
    /// </summary>
    private static List<NonRefStaticFieldEntry> CollectNonRefStaticFields(
        DumpContext ctx, string? filter, HashSet<string>? excludes)
    {
        var result = new List<NonRefStaticFieldEntry>(256);
        // Deduplicate across multiple AppDomains (same field may appear more than once).
        var seen   = new HashSet<(string DeclType, string FieldName)>();

        try
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules)
                {
                    IReadOnlyList<(ulong, int)> typeDefs;
                    try   { typeDefs = module.EnumerateTypeDefToMethodTableMap().ToList(); }
                    catch { continue; }

                    foreach (var (mt, _) in typeDefs)
                    {
                        if (mt == 0) continue;
                        var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                        if (clrType is null) continue;

                        string declType = clrType.Name ?? "<unknown>";
                        if (DumpHelpers.IsSystemType(declType)) continue;
                        if (excludes is not null && excludes.Any(e =>
                            declType.Contains(e, StringComparison.OrdinalIgnoreCase))) continue;
                        if (filter is not null &&
                            !declType.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (var sf in clrType.StaticFields)
                        {
                            if (sf.IsObjectReference) continue;
                            string fieldName = sf.Name ?? "<unknown>";
                            if (!seen.Add((declType, fieldName))) continue;

                            string fieldType   = sf.Type?.Name ?? sf.ElementType.ToString();
                            bool   isEnum      = sf.Type?.IsEnum == true;
                            string elementKind = isEnum ? "Enum"
                                : sf.ElementType switch
                                {
                                    ClrElementType.Boolean or ClrElementType.Char    or
                                    ClrElementType.Int8    or ClrElementType.UInt8   or
                                    ClrElementType.Int16   or ClrElementType.UInt16  or
                                    ClrElementType.Int32   or ClrElementType.UInt32  or
                                    ClrElementType.Int64   or ClrElementType.UInt64  or
                                    ClrElementType.Float   or ClrElementType.Double  => "Primitive",
                                    ClrElementType.NativeInt or ClrElementType.NativeUInt
                                        or ClrElementType.Pointer => "Pointer",
                                    _ => "Struct"
                                };

                            string? value = null;
                            if (elementKind is "Primitive" or "Enum")
                            {
                                try
                                {
                                    value = sf.ElementType switch
                                    {
                                        ClrElementType.Boolean => sf.Read<bool>(appDomain).ToString(),
                                        ClrElementType.Char    => $"'{sf.Read<char>(appDomain)}'",
                                        ClrElementType.Int8    => sf.Read<sbyte>(appDomain).ToString("N0"),
                                        ClrElementType.UInt8   => sf.Read<byte>(appDomain).ToString("N0"),
                                        ClrElementType.Int16   => sf.Read<short>(appDomain).ToString("N0"),
                                        ClrElementType.UInt16  => sf.Read<ushort>(appDomain).ToString("N0"),
                                        ClrElementType.Int32   => sf.Read<int>(appDomain).ToString("N0"),
                                        ClrElementType.UInt32  => sf.Read<uint>(appDomain).ToString("N0"),
                                        ClrElementType.Int64   => sf.Read<long>(appDomain).ToString("N0"),
                                        ClrElementType.UInt64  => sf.Read<ulong>(appDomain).ToString("N0"),
                                        ClrElementType.Float   => sf.Read<float>(appDomain).ToString("G"),
                                        ClrElementType.Double  => sf.Read<double>(appDomain).ToString("G"),
                                        _ => null
                                    };
                                }
                                catch { }
                            }

                            result.Add(new NonRefStaticFieldEntry(declType, fieldName, fieldType, elementKind, value));
                        }
                    }
                }
            }
        }
        catch { }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.DeclType, b.DeclType));
        return result;
    }
}