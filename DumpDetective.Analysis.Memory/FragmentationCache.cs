using DumpDetective.Core.Models.CommandData;
using System.Text;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists the output of <see cref="Analyzers.HeapFragmentationAnalyzer"/> to a small
/// binary file in the <c>.ddcache\&lt;dump-name&gt;\</c> folder so that repeated analyses
/// of the same dump skip the full heap walk (~2 GB working-set delta for large dumps).
///
/// <para>
/// File format (uncompressed, little-endian):
/// <code>
///   Magic     :  4 bytes  "FRAG"
///   Version   :  4 bytes  int
///   DumpLength:  8 bytes  long   — dump file size for staleness detection
///   DumpTicks :  8 bytes  long   — dump last-write UTC ticks
///   SegCount  :  4 bytes  int
///   Per segment:
///     Kind          : length-prefixed UTF-8 string (BinaryWriter.Write)
///     Address       : 8 bytes  ulong
///     CommittedBytes: 8 bytes  long
///     LiveBytes     : 8 bytes  long
///     FreeBytes     : 8 bytes  long
///     PinnedCount   : 4 bytes  int
///   BucketCount:  4 bytes  int
///   Per bucket:
///     Label     : length-prefixed UTF-8 string
///     Count     : 8 bytes  long
///     TotalBytes: 8 bytes  long
///     SortKey   : 4 bytes  int
/// </code>
/// Total size is typically a few KB — no compression needed.
/// </para>
/// </summary>
public static class FragmentationCache
{
    private static readonly byte[] MagicBytes = "FRAG"u8.ToArray(); // 4 bytes
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cache file path: <c>&lt;dump-dir&gt;\.ddcache\&lt;dump-name&gt;\fragmentation.bin</c>.
    /// </summary>
    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "fragmentation.bin");
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when a valid cache exists for <paramref name="dumpPath"/>:
    /// the file must exist and the embedded dump size/timestamp must match the current dump.
    /// </summary>
    public static bool IsValid(string cachePath, string dumpPath)
    {
        if (!File.Exists(cachePath) || !File.Exists(dumpPath)) return false;
        try
        {
            var dumpInfo = new FileInfo(dumpPath);
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4) return false;
            for (int i = 0; i < 4; i++)
                if (magic[i] != MagicBytes[i]) return false;

            if (br.ReadInt32() != CurrentVersion)                    return false;
            if (br.ReadInt64() != dumpInfo.Length)                   return false;
            if (br.ReadInt64() != dumpInfo.LastWriteTimeUtc.Ticks)   return false;
            return true;
        }
        catch { return false; }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the final <see cref="HeapFragmentationData"/> to <paramref name="cachePath"/>
    /// using an atomic temp-file write (.tmp then move).
    /// </summary>
    public static void Save(
        string cachePath, string dumpPath, HeapFragmentationData data)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                          FileShare.None, bufferSize: 64 * 1024);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            fs.Write(MagicBytes);
            bw.Write(CurrentVersion);
            bw.Write(dumpInfo.Length);
            bw.Write(dumpInfo.LastWriteTimeUtc.Ticks);

            bw.Write(data.Segments.Count);
            foreach (var s in data.Segments)
            {
                bw.Write(s.Kind);
                bw.Write(s.Address);
                bw.Write(s.CommittedBytes);
                bw.Write(s.LiveBytes);
                bw.Write(s.FreeBytes);
                bw.Write(s.PinnedCount);
            }

            bw.Write(data.FreeDistribution.Count);
            foreach (var b in data.FreeDistribution)
            {
                bw.Write(b.Label);
                bw.Write(b.Count);
                bw.Write(b.TotalBytes);
                bw.Write(b.SortKey);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        File.Move(tmp, cachePath, overwrite: true);
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a previously saved cache.  Assumes <see cref="IsValid"/> was already checked.
    /// Returns <see langword="null"/> on any read error so the caller can fall back to a
    /// fresh heap walk.
    /// </summary>
    public static HeapFragmentationData? TryLoad(string cachePath)
    {
        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            // Skip magic(4) + version(4) + dumpLen(8) + dumpTicks(8) = 24 bytes header.
            fs.Seek(24, SeekOrigin.Begin);

            int segCount = br.ReadInt32();
            var segments = new List<HeapSegmentInfo>(segCount);
            for (int i = 0; i < segCount; i++)
            {
                string kind           = br.ReadString();
                ulong  address        = br.ReadUInt64();
                long   committedBytes = br.ReadInt64();
                long   liveBytes      = br.ReadInt64();
                long   freeBytes      = br.ReadInt64();
                int    pinnedCount    = br.ReadInt32();
                segments.Add(new HeapSegmentInfo(kind, address, committedBytes, liveBytes, freeBytes, pinnedCount));
            }

            int bucketCount = br.ReadInt32();
            var buckets     = new List<FreeHoleBucket>(bucketCount);
            for (int i = 0; i < bucketCount; i++)
            {
                string label      = br.ReadString();
                long   count      = br.ReadInt64();
                long   totalBytes = br.ReadInt64();
                int    sortKey    = br.ReadInt32();
                buckets.Add(new FreeHoleBucket(label, count, totalBytes, sortKey));
            }

            return new HeapFragmentationData(segments, buckets);
        }
        catch { return null; }
    }
}
