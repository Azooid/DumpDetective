using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis;

/// <summary>
/// CSR (Compressed Sparse Row) forward-reference graph of a managed heap.
/// Built once by <see cref="BfsIndexBuilder"/> and persisted as a <c>.bfs.idx</c> file
/// alongside the dump.  Enables O(N + E) retained-size BFS without any ClrMD I/O at
/// query time.
/// </summary>
public sealed class BfsIndexCache
{
    // ── File format ──────────────────────────────────────────────────────────
    private static readonly byte[] MagicBytes = "BFSIDX1\0"u8.ToArray(); // 8 bytes
    private const int CurrentVersion = 1;

    // ── Node / edge data ─────────────────────────────────────────────────────
    internal readonly ulong[] IndexToAddr;   // node i  → heap address
    internal readonly long[]  Sizes;         // node i  → object size in bytes
    internal readonly int[]   Offsets;       // CSR row starts; length = NodeCount + 1
    internal readonly int[]   Children;      // CSR flat edges;  length = EdgeCount
    private  readonly Dictionary<ulong, int> _addrToIndex;

    internal BfsIndexCache(
        ulong[] indexToAddr, long[] sizes, int[] offsets, int[] children,
        Dictionary<ulong, int> addrToIndex)
    {
        IndexToAddr  = indexToAddr;
        Sizes        = sizes;
        Offsets      = offsets;
        Children     = children;
        _addrToIndex = addrToIndex;
    }

    public int NodeCount => IndexToAddr.Length;
    public int EdgeCount => Children.Length;

    public bool TryGetIndex(ulong addr, out int idx) =>
        _addrToIndex.TryGetValue(addr, out idx);

    // ── Path / validation helpers ────────────────────────────────────────────

    /// <summary>
    /// Returns the cache file path derived from a dump path.
    /// "app.dmp" → "app.bfs.idx"
    /// </summary>
    public static string CachePath(string dumpPath) =>
        Path.ChangeExtension(dumpPath, ".bfs.idx");

    /// <summary>
    /// Returns true when a .bfs.idx file exists and was built from the same dump
    /// (validated by file size and last-write timestamp).
    /// </summary>
    public static bool IsValid(string cachePath, string dumpPath)
    {
        if (!File.Exists(cachePath) || !File.Exists(dumpPath)) return false;
        try
        {
            var dumpInfo = new FileInfo(dumpPath);
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Magic is stored uncompressed so we can peek without Brotli
            var magic = new byte[8];
            if (fs.Read(magic, 0, 8) != 8) return false;
            for (int i = 0; i < 8; i++)
                if (magic[i] != MagicBytes[i]) return false;

            using var brotli = new BrotliStream(fs, CompressionMode.Decompress, leaveOpen: true);
            using var br     = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

            if (br.ReadInt32() != CurrentVersion)                    return false;
            if (br.ReadInt64() != dumpInfo.Length)                   return false;
            if (br.ReadInt64() != dumpInfo.LastWriteTimeUtc.Ticks)   return false;
            return true;
        }
        catch { return false; }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Persists the cache to <paramref name="cachePath"/> using an atomic temp-file write
    /// (writes .bfs.idx.tmp then moves).  File format: 8-byte uncompressed magic + Brotli
    /// compressed binary payload.
    /// </summary>
    public void Save(string cachePath, string dumpPath, Action<string>? update = null)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";

        // Compute total uncompressed payload for progress display:
        //   header(28) + IndexToAddr(N*8) + Sizes(N*8) + Offsets((N+1)*4) + Children(E*4)
        long totalRaw   = 28L + (long)NodeCount * 16 + ((long)NodeCount + 1) * 4 + (long)EdgeCount * 4;
        long writtenRaw = 28L; // header bytes, done immediately
        long nextTick   = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBase   = writtenRaw;
        long rateBaseTick = Stopwatch.GetTimestamp();

        void MaybeTick(string section)
        {
            if (update is null) return;
            long now = Stopwatch.GetTimestamp();
            if (now < nextTick) return;
            long elapsed = now - rateBaseTick;
            long rate    = elapsed > 0 ? (writtenRaw - rateBase) * Stopwatch.Frequency / elapsed : 0;
            rateBase = writtenRaw; rateBaseTick = now;
            nextTick = now + Stopwatch.Frequency / 5;
            update($"Saving {section}: {DumpHelpers.FormatSize(writtenRaw)} / {DumpHelpers.FormatSize(totalRaw)}  •  {DumpHelpers.FormatSize(rate)}/s");
        }

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           bufferSize: 1 << 20))
            {
                fs.Write(MagicBytes); // uncompressed — lets IsValid peek cheaply

                using var brotli = new BrotliStream(fs, CompressionLevel.Optimal, leaveOpen: true);
                using var bw     = new BinaryWriter(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

                bw.Write(CurrentVersion);                  // int
                bw.Write(dumpInfo.Length);                 // long
                bw.Write(dumpInfo.LastWriteTimeUtc.Ticks); // long
                bw.Write(NodeCount);                       // int
                bw.Write(EdgeCount);                       // int
                bw.Flush();

                // Write each array in 4 MB chunks so MaybeTick fires every ~200 ms
                WriteArrayChunked(brotli, IndexToAddr, ref writtenRaw, () => MaybeTick("addresses"));
                WriteArrayChunked(brotli, Sizes,       ref writtenRaw, () => MaybeTick("sizes"));
                WriteArrayChunked(brotli, Offsets,     ref writtenRaw, () => MaybeTick("offsets"));
                WriteArrayChunked(brotli, Children,    ref writtenRaw, () => MaybeTick("edges"));

                brotli.Flush();
            }
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Convenience: checks whether a valid cache exists for <paramref name="dumpPath"/> and
    /// loads it if so.  Returns <see langword="null"/> when no valid cache file is found.
    /// </summary>
    public static BfsIndexCache? TryLoad(string dumpPath, Action<string>? update = null)
    {
        string cachePath = CachePath(dumpPath);
        return IsValid(cachePath, dumpPath) ? Load(cachePath, update) : null;
    }

    /// <summary>
    /// Loads a .bfs.idx file.  Assumes <see cref="IsValid"/> was already checked (or the
    /// caller trusts the file).
    /// </summary>
    public static BfsIndexCache Load(string cachePath, Action<string>? update = null)
    {
        long totalRaw     = 0;
        long readRaw      = 0;
        long nextTick     = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        long rateBase     = 0;
        long rateBaseTick = Stopwatch.GetTimestamp();

        void MaybeTick(string section)
        {
            if (update is null) return;
            long now = Stopwatch.GetTimestamp();
            if (now < nextTick) return;
            long elapsed = now - rateBaseTick;
            long rate    = elapsed > 0 ? (readRaw - rateBase) * Stopwatch.Frequency / elapsed : 0;
            rateBase = readRaw; rateBaseTick = now;
            nextTick = now + Stopwatch.Frequency / 5;
            string total = totalRaw > 0 ? $" / {DumpHelpers.FormatSize(totalRaw)}" : "";
            update($"Loading {section}: {DumpHelpers.FormatSize(readRaw)}{total}  •  {DumpHelpers.FormatSize(rate)}/s");
        }

        using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      bufferSize: 1 << 20);
        fs.Seek(8, SeekOrigin.Begin); // skip uncompressed magic

        using var brotli = new BrotliStream(fs, CompressionMode.Decompress, leaveOpen: true);
        using var br     = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

        /* version   = */ br.ReadInt32();
        /* dumpLen   = */ br.ReadInt64();
        /* dumpTicks = */ br.ReadInt64();

        int nodeCount = br.ReadInt32();
        int edgeCount = br.ReadInt32();

        totalRaw = 28L + (long)nodeCount * 16 + ((long)nodeCount + 1) * 4 + (long)edgeCount * 4;
        readRaw  = 28L;

        var indexToAddr = new ulong[nodeCount];
        ReadArrayChunked(brotli, indexToAddr, ref readRaw, () => MaybeTick("addresses"));

        var sizes = new long[nodeCount];
        ReadArrayChunked(brotli, sizes, ref readRaw, () => MaybeTick("sizes"));

        var offsets = new int[nodeCount + 1];
        ReadArrayChunked(brotli, offsets, ref readRaw, () => MaybeTick("offsets"));

        var children = new int[edgeCount];
        ReadArrayChunked(brotli, children, ref readRaw, () => MaybeTick("edges"));

        var addrToIndex = new Dictionary<ulong, int>(nodeCount);
        for (int i = 0; i < nodeCount; i++) addrToIndex[indexToAddr[i]] = i;

        return new BfsIndexCache(indexToAddr, sizes, offsets, children, addrToIndex);
    }

    // ── Bulk array I/O helpers ───────────────────────────────────────────────

    private const int ChunkBytes = 4 << 20; // 4 MB per write — keeps Brotli fed without blocking too long

    private static void WriteArrayChunked<T>(Stream stream, T[] array, ref long writtenRaw, Action tick)
        where T : struct
    {
        var bytes  = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(array));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int chunk = Math.Min(ChunkBytes, bytes.Length - offset);
            stream.Write(bytes.Slice(offset, chunk));
            offset     += chunk;
            writtenRaw += chunk;
            tick();
        }
    }

    private static void ReadArrayChunked<T>(Stream stream, T[] array, ref long readRaw, Action tick)
        where T : struct
    {
        var bytes  = MemoryMarshal.AsBytes(new Span<T>(array));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int want = Math.Min(ChunkBytes, bytes.Length - offset);
            int got  = stream.Read(bytes.Slice(offset, want));
            if (got == 0) throw new EndOfStreamException("BFS index file truncated.");
            offset  += got;
            readRaw += got;
            tick();
        }
    }

    private static void WriteArray<T>(Stream stream, T[] array) where T : struct
    {
        stream.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(array)));
    }

    private static void ReadArray<T>(Stream stream, T[] array) where T : struct
    {
        var bytes = MemoryMarshal.AsBytes(new Span<T>(array));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes[offset..]);
            if (read == 0) throw new EndOfStreamException("BFS index file truncated.");
            offset += read;
        }
    }

    // ── BFS query ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retained-size BFS from <paramref name="rootAddr"/> using the pre-built index
    /// (zero ClrMD I/O).
    /// <para>
    /// <paramref name="visited"/> is shared across successive calls so each heap node is
    /// attributed to the first field that reaches it (exclusive retained size — prevents
    /// double-counting shared subgraphs).
    /// </para>
    /// </summary>
    public (long Size, bool Estimated) ComputeRetained(
        ulong rootAddr, HashSet<int> visited, long nodeCap = 0)
    {
        if (!_addrToIndex.TryGetValue(rootAddr, out int rootIdx)) return (0, false);
        if (!visited.Add(rootIdx)) return (0, false);

        long retained = Sizes[rootIdx];
        var  stack    = new Stack<int>(64);
        stack.Push(rootIdx);

        while (stack.Count > 0)
        {
            if (nodeCap > 0 && (long)visited.Count >= nodeCap)
            {
                double avg = visited.Count > 0 ? (double)retained / visited.Count : 0;
                return (retained + (long)(stack.Count * avg), true);
            }

            int cur = stack.Pop();
            int s   = Offsets[cur];
            int e   = Offsets[cur + 1];
            for (int i = s; i < e; i++)
            {
                int child = Children[i];
                if (!visited.Add(child)) continue;
                retained += Sizes[child];
                stack.Push(child);
                if (nodeCap > 0 && (long)visited.Count >= nodeCap) break;
            }
        }

        return (retained, false);
    }
}
