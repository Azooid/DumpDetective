using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Collects the raw (address, size) pairs needed to produce a <see cref="BfsPass1State"/>
/// as a by-product of a <see cref="HeapWalker"/> pass.
/// Eliminates the dedicated <see cref="BfsIndexBuilder.BuildPass1"/> heap walk when
/// combined with <see cref="DumpCollector.CollectFull"/>.
/// </summary>
public sealed class BfsPass1Consumer : IHeapObjectConsumer
{
    // Per-clone storage — populated by Consume() during the parallel heap walk.
    private List<ulong> _addrs = new(2_000_000);
    private List<long>  _sizes = new(2_000_000);
    private long _totalBytes;

    // Primary-only: each MergeFrom() call converts a clone's lists to exact-size
    // arrays and stores them here. Avoids List<T>.AddRange doubling which would
    // grow the primary list to 256M-entry backing (4 GB) for 86.5M items (1.4 GB).
    private List<(ulong[] Addrs, long[] Sizes)>? _chunks;

    public bool IsThreadSafe => false;

    public IHeapObjectConsumer CreateClone() => new BfsPass1Consumer();

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        _addrs.Add(obj.Address);
        _sizes.Add((long)obj.Size);
        _totalBytes += (long)obj.Size;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        if (other is not BfsPass1Consumer c) return;
        int cnt = c._addrs.Count;
        _totalBytes += c._totalBytes;
        if (cnt == 0) return;

        // Copy the clone's data into exact-size arrays and free the clone's List<T>
        // backing immediately. List<T>.AddRange would force the primary list to double
        // repeatedly (2M→4M→8M→…→256M = 4 GB for 86.5M objects, wasting 3.3 GB).
        // With 8 bucket clones each at ~10.8M objects the peak saving is ~6.6 GB.
        var addrs = GC.AllocateUninitializedArray<ulong>(cnt);
        var sizes = GC.AllocateUninitializedArray<long>(cnt);
        CollectionsMarshal.AsSpan(c._addrs).CopyTo(addrs);
        CollectionsMarshal.AsSpan(c._sizes).CopyTo(sizes);
        c._addrs.Clear(); c._addrs.TrimExcess();
        c._sizes.Clear(); c._sizes.TrimExcess();

        _chunks ??= new List<(ulong[], long[])>(8);
        _chunks.Add((addrs, sizes));
    }

    public void OnWalkComplete() { /* state built lazily in GetPass1State */ }

    /// <summary>
    /// Builds the <see cref="BfsPass1State"/> from accumulated data.
    /// Must be called after <see cref="HeapWalker.Walk"/> completes.
    /// </summary>
    public BfsPass1State GetPass1State()
    {
        // Count total objects across all sources.
        int n = _addrs.Count; // primary's own entries (0 in normal LoadCommand use)
        if (_chunks is not null)
            foreach (var (a, _) in _chunks) n += a.Length;

        var indexToAddr = GC.AllocateUninitializedArray<ulong>(Math.Max(n, 1));
        var sizes       = GC.AllocateUninitializedArray<long>(Math.Max(n, 1));
        int pos = 0;

        // Copy primary's own entries first (typically empty).
        CollectionsMarshal.AsSpan(_addrs).CopyTo(indexToAddr.AsSpan(pos));
        CollectionsMarshal.AsSpan(_sizes).CopyTo(sizes.AsSpan(pos));
        pos += _addrs.Count;
        _addrs.Clear(); _addrs.TrimExcess();
        _sizes.Clear(); _sizes.TrimExcess();

        // Copy from each chunk and null each entry immediately after copying so the
        // chunk arrays become GC-eligible before the next chunk is processed.
        // This keeps peak at (1 chunk ~172 MB) + indexToAddr + sizes rather than
        // all 8 chunks + indexToAddr + sizes simultaneously.
        if (_chunks is not null)
        {
            for (int k = 0; k < _chunks.Count; k++)
            {
                var (ca, cs) = _chunks[k];
                ca.AsSpan().CopyTo(indexToAddr.AsSpan(pos));
                cs.AsSpan().CopyTo(sizes.AsSpan(pos));
                pos += ca.Length;
                _chunks[k] = default; // let chunk arrays be GC-eligible
            }
            _chunks = null;
        }

        // Build sorted-index map for O(log N) address lookups.
        var sortedAddrs  = GC.AllocateUninitializedArray<ulong>(Math.Max(n, 1));
        var sortedIdxMap = GC.AllocateUninitializedArray<int>(Math.Max(n, 1));
        indexToAddr.AsSpan(0, n).CopyTo(sortedAddrs);
        for (int i = 0; i < n; i++) sortedIdxMap[i] = i;
        Array.Sort(sortedAddrs, sortedIdxMap, 0, n);
        sortedAddrs = null!; // temp address copy no longer needed

        return new BfsPass1State(indexToAddr, sizes, sortedIdxMap, _totalBytes);
    }
}
