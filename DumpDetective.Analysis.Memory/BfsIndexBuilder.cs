using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory;

/// <summary>Intermediate state produced by <see cref="BfsIndexBuilder.BuildPass1"/>.</summary>
public sealed class BfsPass1State
{
    internal readonly ulong[] IndexToAddr;
    internal readonly long[]  Sizes;
    /// <summary>
    /// Index map sorted by address value. <c>SortedIdxMap[i]</c> is the creation-order
    /// index whose address is the i-th smallest. Enables O(log N) address lookups
    /// using 4 bytes × N instead of the 32 bytes × N of a <c>Dictionary&lt;ulong,int&gt;</c>.
    /// Saves ~3.2 GB at 110M objects compared to the old dictionary approach.
    /// </summary>
    internal readonly int[]   SortedIdxMap;
    public   readonly long    TotalBytes;
    public   int NodeCount => IndexToAddr.Length;

    internal BfsPass1State(ulong[] indexToAddr, long[] sizes, int[] sortedIdxMap, long totalBytes)
    {
        IndexToAddr  = indexToAddr;
        Sizes        = sizes;
        SortedIdxMap = sortedIdxMap;
        TotalBytes   = totalBytes;
    }

    /// <summary>O(log N) address-to-index lookup via binary search over <see cref="SortedIdxMap"/>.</summary>
    internal bool TryGetIndex(ulong addr, out int idx)
    {
        int lo = 0, hi = SortedIdxMap.Length - 1;
        while (lo <= hi)
        {
            int   mid    = (lo + hi) >>> 1;
            ulong midKey = IndexToAddr[SortedIdxMap[mid]];
            if (midKey == addr) { idx = SortedIdxMap[mid]; return true; }
            if (midKey <  addr) lo = mid + 1;
            else                hi = mid - 1;
        }
        idx = -1;
        return false;
    }
}

/// <summary>Intermediate state produced by <see cref="BfsIndexBuilder.BuildPass2"/>.</summary>
public sealed class BfsPass2State
{
    internal readonly BfsPass1State Pass1;
    /// <summary>
    /// Per-node outbound edge count. Used only for the prefix-sum that builds
    /// <see cref="BfsIndexCache.Offsets"/> at the start of
    /// <see cref="BfsIndexBuilder.BuildPass3"/>. Released immediately after that
    /// prefix-sum via <see cref="ReleaseChildCounts"/> to free ~440 MB before
    /// the 72-second parallel fill begins.
    /// </summary>
    internal int[]?                 ChildCounts; // non-readonly: BuildPass3 releases it early
    public   readonly long          TotalEdges;

    internal BfsPass2State(BfsPass1State pass1, int[] childCounts, long totalEdges)
    {
        Pass1       = pass1;
        ChildCounts = childCounts;
        TotalEdges  = totalEdges;
    }

    /// <summary>Nulls <see cref="ChildCounts"/> so the 440 MB array is GC-eligible
    /// once the prefix-sum in BuildPass3 is complete.</summary>
    internal void ReleaseChildCounts() => ChildCounts = null;
}

/// <summary>
/// Builds a <see cref="BfsIndexCache"/> from a <see cref="ClrHeap"/> using a 3-pass
/// algorithm (enumerate + count edges + fill CSR) and provides ClrMD-aware retained-size
/// computation over the built index.
/// </summary>
public static class BfsIndexBuilder
{
    /// <summary>
    /// Convenience wrapper: runs all three passes and returns the finished cache.
    /// Use <see cref="BuildPass1"/>/<see cref="BuildPass2"/>/<see cref="BuildPass3"/>
    /// directly when you need a separate spinner line per pass.
    /// </summary>
    public static BfsIndexCache Build(ClrHeap heap, Action<string>? update = null)
    {
        var p1 = BuildPass1(heap, update);
        var p2 = BuildPass2(heap, p1,   update);
        return BuildPass3(heap, p2,   update);
    }

    private const int MaxParallel = 8;

    // ── Sequential-mode detection (used for .NET Core / NativeAOT dumps) ───────
    // .NET Core dump data readers are not thread-safe across segment accesses.
    // Using Parallel.ForEach(heap.Segments) on those dumps causes
    // "The handle is invalid" errors. Fall back to single-threaded iteration.

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="flavor"/> identifies a runtime
    /// whose dump data reader is not thread-safe for concurrent segment access
    /// (.NET Core, NativeAOT).  Extracted for unit-testability.
    /// </summary>
    internal static bool IsSequentialFlavor(ClrFlavor? flavor)
        => flavor is ClrFlavor.Core or ClrFlavor.NativeAOT;

    private static bool IsSequentialOnly(ClrHeap heap)
        => IsSequentialFlavor(heap.Runtime?.ClrInfo?.Flavor);

    // ── Pass 1: assign stable integer indices, collect sizes ─────────────────
    // Phase 1a: each segment collects its own (addr, size) list in parallel.
    // Phase 1b: segments are merged in order → deterministic index assignment.

    public static BfsPass1State BuildPass1(ClrHeap heap, Action<string>? update = null)
    {
        if (IsSequentialOnly(heap))
            return BuildPass1Sequential(heap, update);

        var segments = heap.Segments.ToArray();
        var perSeg   = new (ulong[] Addrs, long[] Sizes)[segments.Length];

        long totalObjsAtomic  = 0;
        long totalBytesAtomic = 0;
        long nextTick         = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBaseCount    = 0;
        long rateBaseTick     = Stopwatch.GetTimestamp();

        Parallel.For(0, segments.Length,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel },
            i =>
            {
                var  addrBuf  = new List<ulong>(4096);
                var  sizeBuf  = new List<long>(4096);
                long segBytes = 0;
                long segCount = 0;

                foreach (var obj in segments[i].EnumerateObjects())
                {
                    if (!obj.IsValid || obj.IsNull) continue;
                    addrBuf.Add(obj.Address);
                    long sz = (long)obj.Size;
                    sizeBuf.Add(sz);
                    segBytes += sz;
                    segCount++;

                    // flush to shared atomics + try to update spinner every 64K objects
                    if (update is not null && (segCount & 0xFFFF) == 0)
                    {
                        long t = Interlocked.Add(ref totalObjsAtomic,  segCount);
                        long b = Interlocked.Add(ref totalBytesAtomic, segBytes);
                        segCount = 0; segBytes = 0;

                        long now  = Stopwatch.GetTimestamp();
                        long tick = Interlocked.Read(ref nextTick);
                        if (now >= tick && Interlocked.CompareExchange(ref nextTick, now + Stopwatch.Frequency / 5, tick) == tick)
                        {
                            long elapsed = now - rateBaseTick;
                            long rate    = elapsed > 0 ? (t - rateBaseCount) * Stopwatch.Frequency / elapsed : 0;
                            rateBaseCount = t; rateBaseTick = now;
                            update($"pass 1/3 — enumerating: {t:N0} objects  •  {DumpHelpers.FormatSize(b)}  •  {rate:N0}/s");
                        }
                    }
                }

                // flush residual
                Interlocked.Add(ref totalObjsAtomic,  segCount);
                Interlocked.Add(ref totalBytesAtomic, segBytes);
                perSeg[i] = (addrBuf.ToArray(), sizeBuf.ToArray());
            });

        // Phase 1b: sequential merge — stable order = stable indices.
        // GC.AllocateUninitializedArray: both arrays are fully overwritten in the loop.
        // Null each perSeg entry immediately after merging so the GC can reclaim those
        // intermediate arrays progressively rather than keeping all of them alive until
        // the entire loop completes (saves ~segCount × avg_seg_size × 16 bytes peak).
        int totalNodes  = (int)Interlocked.Read(ref totalObjsAtomic);
        var indexToAddr = GC.AllocateUninitializedArray<ulong>(Math.Max(totalNodes, 1));
        var sizes       = GC.AllocateUninitializedArray<long>(Math.Max(totalNodes, 1));
        int pos = 0;
        for (int i = 0; i < perSeg.Length; i++)
        {
            var (addrs, szs) = perSeg[i];
            perSeg[i] = default; // release this segment's arrays — GC can collect them now
            for (int j = 0; j < addrs.Length; j++)
            {
                indexToAddr[pos] = addrs[j];
                sizes[pos]       = szs[j];
                pos++;
            }
        }

        // Build sorted-index map: sort a copy of addresses alongside 0..N-1 index map.
        // Array.Sort(TKey[], TValue[]) uses intrinsics (faster than comparator lambda)
        // and is the same sort BfsIndexCache used to do in its constructor — we now do
        // it here so BuildPass3 can reuse SortedIdxMap as _sortedIdxMap, saving ~440 MB
        // and the sort time in the constructor.
        var sortedAddrs  = GC.AllocateUninitializedArray<ulong>(Math.Max(totalNodes, 1));
        var sortedIdxMap = GC.AllocateUninitializedArray<int>(Math.Max(totalNodes, 1));
        indexToAddr.AsSpan(0, totalNodes).CopyTo(sortedAddrs);
        for (int i = 0; i < totalNodes; i++) sortedIdxMap[i] = i;
        Array.Sort(sortedAddrs, sortedIdxMap, 0, totalNodes);
        // sortedAddrs is a temporary needed only for the sort; release it immediately.
        sortedAddrs = null!;

        return new BfsPass1State(indexToAddr, sizes, sortedIdxMap, totalBytesAtomic);
    }

    // ── Pass 2: count forward edges per node (parallel segments) ─────────────
    // Each object lives in exactly one segment, so childCounts[pIdx] writes
    // are never concurrent — no Interlocked needed on the per-node array.

    public static BfsPass2State BuildPass2(ClrHeap heap, BfsPass1State p1, Action<string>? update = null)
    {
        if (IsSequentialOnly(heap))
            return BuildPass2Sequential(heap, p1, update);

        int  nodeCount     = p1.NodeCount;
        var  childCounts   = new int[nodeCount];
        long totalEdges    = 0;
        long scanned       = 0;
        long nextTick      = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBaseCount = 0;
        long rateBaseTick  = Stopwatch.GetTimestamp();

        Parallel.ForEach(heap.Segments,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel },
            seg =>
            {
                long localEdges   = 0;
                long localScanned = 0;

                foreach (var obj in seg.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.IsNull ||
                        !p1.TryGetIndex(obj.Address, out int pIdx)) continue;

                    localScanned++;
                    foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                    {
                        if (childAddr == 0 || !p1.TryGetIndex(childAddr, out _)) continue;
                        childCounts[pIdx]++; // safe: pIdx is unique per thread
                        localEdges++;
                    }

                    if (update is not null && (localScanned & 0xFFFF) == 0)
                    {
                        long s = Interlocked.Add(ref scanned,    localScanned); localScanned = 0;
                        long e = Interlocked.Add(ref totalEdges, localEdges);   localEdges   = 0;

                        long now  = Stopwatch.GetTimestamp();
                        long tick = Interlocked.Read(ref nextTick);
                        if (now >= tick && Interlocked.CompareExchange(ref nextTick, now + Stopwatch.Frequency / 5, tick) == tick)
                        {
                            long elapsed = now - rateBaseTick;
                            long rate    = elapsed > 0 ? (s - rateBaseCount) * Stopwatch.Frequency / elapsed : 0;
                            rateBaseCount = s; rateBaseTick = now;
                            update($"pass 2/3 — counting edges: {s:N0}/{nodeCount:N0} objects  •  {e:N0} edges  •  {rate:N0}/s");
                        }
                    }
                }

                // flush residual
                Interlocked.Add(ref scanned,    localScanned);
                Interlocked.Add(ref totalEdges, localEdges);
            });

        if (totalEdges > int.MaxValue)
            throw new InvalidOperationException(
                $"Edge count {totalEdges:N0} exceeds int.MaxValue — dump is too large for int-indexed CSR.");

        return new BfsPass2State(p1, childCounts, totalEdges);
    }

    // ── Pass 3: fill CSR edge arrays (parallel segments) ──────────────────────
    // writeCursor[pIdx] writes are never concurrent for the same reason as pass 2.

    public static BfsIndexCache BuildPass3(ClrHeap heap, BfsPass2State p2, Action<string>? update = null)
    {
        if (IsSequentialOnly(heap))
            return BuildPass3Sequential(heap, p2, update);

        var p1        = p2.Pass1;
        int nodeCount = p1.NodeCount;

        // offsets[0] must be 0 to seed the prefix-sum loop; the rest are fully written.
        var offsets = GC.AllocateUninitializedArray<int>(nodeCount + 1);
        offsets[0] = 0;
        for (int i = 0; i < nodeCount; i++)
            offsets[i + 1] = offsets[i] + p2.ChildCounts![i];

        // ChildCounts is only needed for the prefix-sum above.
        // Release it now so the ~440 MB array is GC-eligible before the
        // 72-second parallel fill begins (pass 3 is the dominant memory consumer).
        p2.ReleaseChildCounts();

        // children: pure positional write — never read before being written.
        // writeCursor: fully overwritten by Array.Copy below.
        var children    = GC.AllocateUninitializedArray<int>((int)p2.TotalEdges);
        var writeCursor = GC.AllocateUninitializedArray<int>(nodeCount);
        Array.Copy(offsets, writeCursor, nodeCount);

        long scanned       = 0;
        long nextTick      = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBaseCount = 0;
        long rateBaseTick  = Stopwatch.GetTimestamp();

        Parallel.ForEach(heap.Segments,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel },
            seg =>
            {
                long localScanned = 0;

                foreach (var obj in seg.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.IsNull ||
                        !p1.TryGetIndex(obj.Address, out int pIdx)) continue;

                    localScanned++;
                    foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                    {
                        if (childAddr == 0 || !p1.TryGetIndex(childAddr, out int cIdx)) continue;
                        children[writeCursor[pIdx]++] = cIdx; // safe: pIdx unique per thread
                    }

                    if (update is not null && (localScanned & 0xFFFF) == 0)
                    {
                        long s    = Interlocked.Add(ref scanned, localScanned); localScanned = 0;
                        long now  = Stopwatch.GetTimestamp();
                        long tick = Interlocked.Read(ref nextTick);
                        if (now >= tick && Interlocked.CompareExchange(ref nextTick, now + Stopwatch.Frequency / 5, tick) == tick)
                        {
                            long elapsed = now - rateBaseTick;
                            long rate    = elapsed > 0 ? (s - rateBaseCount) * Stopwatch.Frequency / elapsed : 0;
                            rateBaseCount = s; rateBaseTick = now;
                            update($"pass 3/3 — filling edges: {s:N0}/{nodeCount:N0} objects  •  {rate:N0}/s");
                        }
                    }
                }

                Interlocked.Add(ref scanned, localScanned);
            });

        // SortedIdxMap from pass 1 is identical to what BfsIndexCache constructor
        // used to build internally — reuse it directly to skip an 880 MB sort.
        return new BfsIndexCache(p1.IndexToAddr, p1.Sizes, offsets, children, p1.SortedIdxMap);
    }

    // ── Sequential fallbacks (used when IsSequentialOnly returns true) ────────
    // These re-implement each pass using heap.EnumerateObjects() instead of
    // Parallel.ForEach(heap.Segments) to avoid "The handle is invalid" on Core dumps.

    private static BfsPass1State BuildPass1Sequential(ClrHeap heap, Action<string>? update)
    {
        var addrBuf   = new List<ulong>(65536);
        var sizeBuf   = new List<long>(65536);
        long totBytes = 0;
        long count    = 0;
        long nextTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBase = 0;
        long rateTick = Stopwatch.GetTimestamp();

        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull) continue;
            addrBuf.Add(obj.Address);
            long sz = (long)obj.Size;
            sizeBuf.Add(sz);
            totBytes += sz;
            count++;

            if (update is not null && (count & 0x3FFF) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    long elapsed = now - rateTick;
                    long rate    = elapsed > 0 ? (count - rateBase) * Stopwatch.Frequency / elapsed : 0;
                    rateBase = count; rateTick = now;
                    nextTick = now + Stopwatch.Frequency / 5;
                    update($"pass 1/3 — enumerating: {count:N0} objects  •  {DumpHelpers.FormatSize(totBytes)}  •  {rate:N0}/s");
                }
            }
        }

        int totalNodes  = addrBuf.Count;
        var indexToAddr = addrBuf.ToArray();
        var sizes       = sizeBuf.ToArray();
        var sortedAddrs = GC.AllocateUninitializedArray<ulong>(Math.Max(totalNodes, 1));
        var sortedIdxMap= GC.AllocateUninitializedArray<int>(Math.Max(totalNodes, 1));
        indexToAddr.AsSpan(0, totalNodes).CopyTo(sortedAddrs);
        for (int i = 0; i < totalNodes; i++) sortedIdxMap[i] = i;
        Array.Sort(sortedAddrs, sortedIdxMap, 0, totalNodes);
        sortedAddrs = null!;
        return new BfsPass1State(indexToAddr, sizes, sortedIdxMap, totBytes);
    }

    private static BfsPass2State BuildPass2Sequential(ClrHeap heap, BfsPass1State p1, Action<string>? update)
    {
        int  nodeCount  = p1.NodeCount;
        var  childCounts= new int[nodeCount];
        long totalEdges = 0;
        long scanned    = 0;
        long nextTick   = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBase   = 0;
        long rateTick   = Stopwatch.GetTimestamp();

        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || !p1.TryGetIndex(obj.Address, out int pIdx)) continue;
            scanned++;

            foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (childAddr == 0 || !p1.TryGetIndex(childAddr, out _)) continue;
                childCounts[pIdx]++;
                totalEdges++;
            }

            if (update is not null && (scanned & 0x3FFF) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    long elapsed = now - rateTick;
                    long rate    = elapsed > 0 ? (scanned - rateBase) * Stopwatch.Frequency / elapsed : 0;
                    rateBase = scanned; rateTick = now;
                    nextTick = now + Stopwatch.Frequency / 5;
                    update($"pass 2/3 — counting edges: {scanned:N0}/{nodeCount:N0} objects  •  {totalEdges:N0} edges  •  {rate:N0}/s");
                }
            }
        }

        if (totalEdges > int.MaxValue)
            throw new InvalidOperationException(
                $"Edge count {totalEdges:N0} exceeds int.MaxValue — dump is too large for int-indexed CSR.");

        return new BfsPass2State(p1, childCounts, totalEdges);
    }

    private static BfsIndexCache BuildPass3Sequential(ClrHeap heap, BfsPass2State p2, Action<string>? update)
    {
        var p1        = p2.Pass1;
        int nodeCount = p1.NodeCount;

        var offsets = GC.AllocateUninitializedArray<int>(nodeCount + 1);
        offsets[0] = 0;
        for (int i = 0; i < nodeCount; i++)
            offsets[i + 1] = offsets[i] + p2.ChildCounts![i];

        p2.ReleaseChildCounts();

        var children    = GC.AllocateUninitializedArray<int>((int)p2.TotalEdges);
        var writeCursor = GC.AllocateUninitializedArray<int>(nodeCount);
        Array.Copy(offsets, writeCursor, nodeCount);

        long scanned  = 0;
        long nextTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBase = 0;
        long rateTick = Stopwatch.GetTimestamp();

        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || !p1.TryGetIndex(obj.Address, out int pIdx)) continue;
            scanned++;

            foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (childAddr == 0 || !p1.TryGetIndex(childAddr, out int cIdx)) continue;
                children[writeCursor[pIdx]++] = cIdx;
            }

            if (update is not null && (scanned & 0x3FFF) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    long elapsed = now - rateTick;
                    long rate    = elapsed > 0 ? (scanned - rateBase) * Stopwatch.Frequency / elapsed : 0;
                    rateBase = scanned; rateTick = now;
                    nextTick = now + Stopwatch.Frequency / 5;
                    update($"pass 3/3 — filling edges: {scanned:N0}/{nodeCount:N0} objects  •  {rate:N0}/s");
                }
            }
        }

        return new BfsIndexCache(p1.IndexToAddr, p1.Sizes, offsets, children, p1.SortedIdxMap);
    }


    /// Computes exclusive retained sizes for each object-reference field of
    /// <paramref name="obj"/> using <paramref name="cache"/> (no ClrMD heap I/O after
    /// reading the field values).
    /// <para>
    /// A single shared <c>visited</c> set across all fields ensures each heap node is
    /// credited to the first field that claims it (exclusive retained — no double-counting
    /// of shared subgraphs such as two fields pointing into the same large dictionary).
    /// </para>
    /// </summary>
    public static Dictionary<string, (long Bytes, bool Estimated)> ComputeRetainedForFields(
        BfsIndexCache cache, ClrObject obj, long nodeCap, Action<string>? update = null)
    {
        var result  = new Dictionary<string, (long, bool)>(StringComparer.Ordinal);
        var visited = new HashSet<int>(1024);
        var type    = obj.Type;
        if (type is null || type.IsString || type.IsArray) return result;

        int  fieldNum      = 0;
        int  totalRefFields = type.Fields.Count(f => f.IsObjectReference);
        long totalVisited   = 0;
        long rateBase       = 0;
        long rateBaseTick   = Stopwatch.GetTimestamp();
        foreach (var field in type.Fields)
        {
            if (!field.IsObjectReference) continue;
            string fn = field.Name ?? "<unknown>";
            try
            {
                var refObj = obj.ReadObjectField(fn);
                if (refObj.IsNull || !refObj.IsValid) continue;

                fieldNum++;
                if (update is not null)
                {
                    long now     = Stopwatch.GetTimestamp();
                    long elapsed = now - rateBaseTick;
                    long rate    = elapsed > 0 ? (totalVisited - rateBase) * Stopwatch.Frequency / elapsed : 0;
                    rateBase = totalVisited; rateBaseTick = now;
                    update($"retained field {fieldNum}/{totalRefFields}: [{fn}]  •  {totalVisited:N0} nodes  •  {rate:N0}/s");
                }

                var (bytes, est) = cache.ComputeRetained(refObj.Address, visited, nodeCap);
                totalVisited = visited.Count;
                result[fn]   = (bytes, est);
            }
            catch { }
        }
        return result;
    }
}
