using DumpDetective.Analysis.Memory.Analyzers;

using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Disk-backed, read-only map from child-object address to its single parent address,
/// for BFS root-chain tracing in <c>MemoryLeakAnalyzer</c>.
///
/// <para>
/// Replaces the in-memory <c>Dictionary&lt;ulong, ParentSlots&gt;</c>
/// (≈ 2.56 GB for 80M objects).  On disk the data is a flat sorted binary file:
/// <code>
///   Header  : long count                         (8 bytes)
///   Children: ulong[count] sorted child addrs     (count × 8 bytes)
///   Parents : ulong[count] corresponding parents  (count × 8 bytes)
/// </code>
/// Total on-disk: 8 + 16 × count bytes (≈ 1.28 GB at 80M entries).
/// </para>
///
/// <para>
/// Lookups use binary search over the memory-mapped Children section.
/// For root-chain tracing the total call count is tiny (≤ depth × samples × suspects
/// ≈ 60 × 3 × 50 = 9 000), so only the hot BFS path stays resident in OS page cache.
/// </para>
/// </summary>
internal sealed class DiskBackedParentMap : IDisposable
{
    private MemoryMappedFile?           _mmf;
    private MemoryMappedViewAccessor?   _view;
    private readonly long               _count;
    private readonly string             _filePath;
    private readonly bool               _deleteOnDispose;

    // Byte offsets within the file.
    private const int HeaderBytes  = 8;    // sizeof(long)
    private long ChildrenOffset    => HeaderBytes;
    private long ParentsOffset     => HeaderBytes + _count * 8L;

    // ── Path ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cache file path inside the <c>.ddcache</c> folder.
    /// "D:\dumps\app.dmp" → "D:\dumps\.ddcache\app\app.parent.map"
    /// </summary>
    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, dumpName + ".parent.map");
    }

    // ── Construction ──────────────────────────────────────────────────────────

    private DiskBackedParentMap(
        MemoryMappedFile mmf, MemoryMappedViewAccessor view,
        long count, string filePath, bool deleteOnDispose)
    {
        _mmf            = mmf;
        _view           = view;
        _count          = count;
        _filePath       = filePath;
        _deleteOnDispose = deleteOnDispose;
    }

    /// <summary>
    /// Writes all (child→parent) entries from <paramref name="stripes"/> to
    /// <paramref name="filePath"/> (sorted by child address) and opens the
    /// resulting file as a <see cref="DiskBackedParentMap"/> ready for lookups.
    /// </summary>
    /// <param name="stripes">
    /// Per-stripe dictionaries from <c>ReferrerConsumer</c>.  Consumed in-place
    /// (each stripe is cleared after its entries are copied to avoid peak duplication).
    /// </param>
    /// <param name="filePath">Destination path; will be overwritten if it exists.</param>
    public static DiskBackedParentMap Write(
        Dictionary<ulong, ParentSlots>[] stripes,
        string filePath,
        bool deleteOnDispose = false)
    {
        // 1. Count total entries.
        int total = 0;
        for (int i = 0; i < stripes.Length; i++) total += stripes[i].Count;

        // 2. Collect into parallel arrays (cheaper than allocating struct array).
        var children = GC.AllocateUninitializedArray<ulong>(total);
        var parents  = GC.AllocateUninitializedArray<ulong>(total);
        int pos = 0;
        for (int i = 0; i < stripes.Length; i++)
        {
            foreach (var (child, ps) in stripes[i])
            {
                children[pos] = child;
                parents[pos]  = ps.Get(0);
                pos++;
            }
            stripes[i].Clear(); // release stripe backing array progressively
            stripes[i] = null!;
        }

        // 3. Sort by child address — needed for binary search at lookup time.
        Array.Sort(children, parents);

        // 4. Write to disk.
        string tmp = filePath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, bufferSize: 1 << 20))
            {
                Span<byte> header = stackalloc byte[8];
                MemoryMarshal.Write(header, in total);  // long count (reinterpret int as long-width)
                // Write actual count as long.
                long countLong = total;
                MemoryMarshal.Write(header, in countLong);
                fs.Write(header);
                fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(children)));
                fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(parents)));
            }
            File.Move(tmp, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        // 5. Open as memory-mapped file.
        return OpenCore(filePath, deleteOnDispose);
    }

    /// <summary>
    /// Writes a pre-sorted (child→parent) pair array directly to disk without
    /// re-sorting. <paramref name="sortedChildren"/> must already be sorted ascending.
    /// Called by <see cref="SharedReferrerCache"/> when the parent map is derived
    /// from the BFS CSR index — no heap walk required.
    /// </summary>
    public static DiskBackedParentMap WriteFromSortedArrays(
        ulong[] sortedChildren, ulong[] parents, int count, string filePath,
        bool deleteOnDispose = false)
    {
        string tmp = filePath + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, bufferSize: 1 << 20))
            {
                Span<byte> header = stackalloc byte[8];
                long countLong = count;
                MemoryMarshal.Write(header, in countLong);
                fs.Write(header);
                fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(sortedChildren, 0, count)));
                fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(parents,        0, count)));
            }
            File.Move(tmp, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return OpenCore(filePath, deleteOnDispose);
    }

    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="parentAddr"/> when
    /// <paramref name="childAddr"/> has a recorded parent in the map.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetParent(ulong childAddr, out ulong parentAddr)
    {
        if (_count == 0 || _view is null) { parentAddr = 0; return false; }

        // Binary search over the Children section.
        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid    = (lo + hi) >> 1;
            ulong midKey = _view.ReadUInt64(ChildrenOffset + mid * 8L);
            if (midKey == childAddr)
            {
                parentAddr = _view.ReadUInt64(ParentsOffset + mid * 8L);
                return true;
            }
            if (midKey < childAddr) lo = mid + 1;
            else                    hi = mid - 1;
        }
        parentAddr = 0;
        return false;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _view?.Dispose();  _view = null;
        _mmf?.Dispose();   _mmf  = null;
        if (_deleteOnDispose)
        {
            try { File.Delete(_filePath); } catch { }
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an existing parent-map file for read-only lookups without writing anything.
    /// Returns <see langword="null"/> if the file does not exist, is too small, or cannot be opened.
    /// </summary>
    public static DiskBackedParentMap? TryLoad(string filePath, bool deleteOnDispose = false)
    {
        if (!File.Exists(filePath)) return null;
        try { return OpenCore(filePath, deleteOnDispose); }
        catch { return null; }
    }

    private static DiskBackedParentMap OpenCore(string filePath, bool deleteOnDispose)
    {
        var fi   = new FileInfo(filePath);
        long fileSize = fi.Length;

        // Read count from header without mapping the whole file.
        long count;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> header = stackalloc byte[8];
            if (fs.Read(header) != 8)
                throw new InvalidDataException("DiskBackedParentMap: file too short.");
            count = MemoryMarshal.Read<long>(header);
        }

        var mmf  = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, null, fileSize, MemoryMappedFileAccess.Read);
        var view = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        return new DiskBackedParentMap(mmf, view, count, filePath, deleteOnDispose);
    }
}
