using DumpDetective.Core.Utilities;
using System.IO.Compression;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists and loads the LT dominator-tree result for a dump:
/// <c>idom[]</c> (immediate-dominator node index per object) and
/// <c>retained[]</c> (dominator-subtree retained bytes per object).
///
/// <para>File format: 8-byte uncompressed magic + Brotli-compressed binary payload.</para>
/// <code>
///   Magic     : "IDOMIDX1"  (8 bytes, uncompressed — lets IsValid peek cheaply)
///   Version   : int
///   DumpLength: long
///   DumpTicks : long
///   NodeCount : int         — heap objects only (virtual root not stored)
///   idom[]    : int[NodeCount]   — dominator index; <see cref="VirtualRootSentinel"/> = GC-root top-level
///   retained[]: long[NodeCount]  — retained bytes in dominator subtree
/// </code>
/// </summary>
public sealed class DomTreeCache
{
    private static readonly byte[] MagicBytes    = "IDOMIDX1"u8.ToArray();
    private const int              CurrentVersion = 1;

    /// <summary>
    /// idom[] value for objects directly dominated by the virtual GC root
    /// (i.e., objects reachable directly from a GC handle, static field, or thread stack).
    /// Equal to <see cref="NodeCount"/> so callers can distinguish from valid node indices.
    /// </summary>
    public readonly int NodeCount;

    /// <summary>idom[i]: immediate dominator node index, or -1 if unreachable.</summary>
    public readonly int[] Idom;

    /// <summary>retained[i]: total bytes retained through i's dominator subtree.</summary>
    public readonly long[] Retained;

    /// <summary>idom value that marks a top-level object (dominated by virtual GC root).</summary>
    public int VirtualRootSentinel => NodeCount;

    public DomTreeCache(int nodeCount, int[] idom, long[] retained)
    {
        NodeCount = nodeCount;
        Idom      = idom;
        Retained  = retained;
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, dumpName + ".idom.idx");
    }

    public static bool IsValid(string cachePath, string dumpPath)
    {
        if (!File.Exists(cachePath) || !File.Exists(dumpPath)) return false;
        try
        {
            var dumpInfo = new FileInfo(dumpPath);
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[8];
            if (fs.Read(magic) != 8) return false;
            for (int i = 0; i < 8; i++)
                if (magic[i] != MagicBytes[i]) return false;

            using var brotli = new BrotliStream(fs, CompressionMode.Decompress, leaveOpen: true);
            using var br     = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);
            if (br.ReadInt32() != CurrentVersion)                  return false;
            if (br.ReadInt64() != dumpInfo.Length)                 return false;
            if (br.ReadInt64() != dumpInfo.LastWriteTimeUtc.Ticks) return false;
            return true;
        }
        catch { return false; }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    public void Save(string cachePath, string dumpPath, Action<string>? update = null)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        long totalRaw   = 28L + (long)NodeCount * 4 + (long)NodeCount * 8; // header + idom + retained
        long writtenRaw = 28L;
        long nextTick   = System.Diagnostics.Stopwatch.GetTimestamp() +
                          System.Diagnostics.Stopwatch.Frequency / 5;
        long rateBase   = 28L;
        long rateBaseTick = System.Diagnostics.Stopwatch.GetTimestamp();

        void MaybeTick(string section)
        {
            if (update is null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < nextTick) return;
            long elapsed = now - rateBaseTick;
            long rate    = elapsed > 0 ? (writtenRaw - rateBase) * System.Diagnostics.Stopwatch.Frequency / elapsed : 0;
            rateBase = writtenRaw; rateBaseTick = now;
            nextTick = now + System.Diagnostics.Stopwatch.Frequency / 5;
            update($"Saving dominator index {section}: {DumpHelpers.FormatSize(writtenRaw)} / {DumpHelpers.FormatSize(totalRaw)}  •  {DumpHelpers.FormatSize(rate)}/s");
        }

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           bufferSize: 1 << 20))
            {
                fs.Write(MagicBytes);

                using var brotli = new BrotliStream(fs, CompressionLevel.Optimal, leaveOpen: true);
                using var bw     = new BinaryWriter(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

                bw.Write(CurrentVersion);
                bw.Write(dumpInfo.Length);
                bw.Write(dumpInfo.LastWriteTimeUtc.Ticks);
                bw.Write(NodeCount);

                // Write idom[] in 1M-entry chunks
                const int ChunkSize = 1 << 20;
                for (int off = 0; off < NodeCount; off += ChunkSize)
                {
                    int count = Math.Min(ChunkSize, NodeCount - off);
                    var span  = System.Runtime.InteropServices.MemoryMarshal.AsBytes(Idom.AsSpan(off, count));
                    brotli.Write(span);
                    writtenRaw += span.Length;
                    MaybeTick("idom");
                }

                for (int off = 0; off < NodeCount; off += ChunkSize)
                {
                    int count = Math.Min(ChunkSize, NodeCount - off);
                    var span  = System.Runtime.InteropServices.MemoryMarshal.AsBytes(Retained.AsSpan(off, count));
                    brotli.Write(span);
                    writtenRaw += span.Length;
                    MaybeTick("retained");
                }

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

    // ── Load ─────────────────────────────────────────────────────────────────

    public static DomTreeCache? TryLoad(string dumpPath, Action<string>? update = null)
    {
        string cachePath = CachePath(dumpPath);
        if (!IsValid(cachePath, dumpPath)) return null;

        try
        {
            update?.Invoke("Loading dominator index…");
            using var fs     = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              bufferSize: 1 << 20);
            fs.Seek(8, SeekOrigin.Begin); // skip magic
            using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
            using var br     = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

            br.ReadInt32(); // version (validated by IsValid)
            br.ReadInt64(); // DumpLength
            br.ReadInt64(); // DumpTicks
            int nodeCount = br.ReadInt32();

            var idom     = new int[nodeCount];
            var retained = new long[nodeCount];

            ReadFull(brotli, System.Runtime.InteropServices.MemoryMarshal.AsBytes(idom.AsSpan()));
            ReadFull(brotli, System.Runtime.InteropServices.MemoryMarshal.AsBytes(retained.AsSpan()));

            return new DomTreeCache(nodeCount, idom, retained);
        }
        catch { return null; }
    }

    private static void ReadFull(Stream s, Span<byte> buf)
    {
        int remaining = buf.Length;
        int offset    = 0;
        while (remaining > 0)
        {
            int n = s.Read(buf.Slice(offset, remaining));
            if (n <= 0) throw new EndOfStreamException();
            offset    += n;
            remaining -= n;
        }
    }
}
