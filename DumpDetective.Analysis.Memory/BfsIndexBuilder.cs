using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory;

/// <summary>Intermediate state produced by <see cref="BfsIndexBuilder.BuildPass1"/>.</summary>
public sealed class BfsPass1State
{
    internal readonly ulong[]               IndexToAddr;
    internal readonly long[]                Sizes;
    internal readonly Dictionary<ulong,int> AddrToIndex;
    public   readonly long                  TotalBytes;
    public   int NodeCount => IndexToAddr.Length;

    internal BfsPass1State(ulong[] indexToAddr, long[] sizes,
                           Dictionary<ulong,int> addrToIndex, long totalBytes)
    {
        IndexToAddr = indexToAddr;
        Sizes       = sizes;
        AddrToIndex = addrToIndex;
        TotalBytes  = totalBytes;
    }
}

/// <summary>Intermediate state produced by <see cref="BfsIndexBuilder.BuildPass2"/>.</summary>
public sealed class BfsPass2State
{
    internal readonly BfsPass1State Pass1;
    internal readonly int[]         ChildCounts;
    public   readonly long          TotalEdges;

    internal BfsPass2State(BfsPass1State pass1, int[] childCounts, long totalEdges)
    {
        Pass1       = pass1;
        ChildCounts = childCounts;
        TotalEdges  = totalEdges;
    }
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

    // ── Pass 1: assign stable integer indices, collect sizes ─────────────────
    // Phase 1a: each segment collects its own (addr, size) list in parallel.
    // Phase 1b: segments are merged in order → deterministic index assignment.

    public static BfsPass1State BuildPass1(ClrHeap heap, Action<string>? update = null)
    {
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

        // Phase 1b: sequential merge — stable order = stable indices
        int totalNodes  = (int)Interlocked.Read(ref totalObjsAtomic);
        var indexToAddr = new ulong[totalNodes];
        var sizes       = new long[totalNodes];
        var addrToIndex = new Dictionary<ulong, int>(totalNodes);
        int pos = 0;
        foreach (var (addrs, szs) in perSeg)
        {
            for (int j = 0; j < addrs.Length; j++)
            {
                addrToIndex[addrs[j]] = pos;
                indexToAddr[pos]      = addrs[j];
                sizes[pos]            = szs[j];
                pos++;
            }
        }

        return new BfsPass1State(indexToAddr, sizes, addrToIndex, totalBytesAtomic);
    }

    // ── Pass 2: count forward edges per node (parallel segments) ─────────────
    // Each object lives in exactly one segment, so childCounts[pIdx] writes
    // are never concurrent — no Interlocked needed on the per-node array.

    public static BfsPass2State BuildPass2(ClrHeap heap, BfsPass1State p1, Action<string>? update = null)
    {
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
                        !p1.AddrToIndex.TryGetValue(obj.Address, out int pIdx)) continue;

                    localScanned++;
                    foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                    {
                        if (childAddr == 0 || !p1.AddrToIndex.ContainsKey(childAddr)) continue;
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
        var p1        = p2.Pass1;
        int nodeCount = p1.NodeCount;

        var offsets = new int[nodeCount + 1];
        for (int i = 0; i < nodeCount; i++)
            offsets[i + 1] = offsets[i] + p2.ChildCounts[i];

        var children    = new int[(int)p2.TotalEdges];
        var writeCursor = new int[nodeCount];
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
                        !p1.AddrToIndex.TryGetValue(obj.Address, out int pIdx)) continue;

                    localScanned++;
                    foreach (var childAddr in obj.EnumerateReferenceAddresses(carefully: false))
                    {
                        if (childAddr == 0 || !p1.AddrToIndex.TryGetValue(childAddr, out int cIdx)) continue;
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

        return new BfsIndexCache(p1.IndexToAddr, p1.Sizes, offsets, children, p1.AddrToIndex);
    }

    /// <summary>
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
