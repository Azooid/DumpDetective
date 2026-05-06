using System.Runtime.InteropServices;
using System.Text;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists the set of statically-rooted object addresses produced by
/// <see cref="Analyzers.StaticRootAddresses.Build"/> to a compact binary file in
/// <c>.ddcache\&lt;dump-name&gt;\</c>.
///
/// <para>
/// Enumerating all app domains → modules → types → static fields is typically 200–250s on a
/// large heap.  The resulting address set is typically a few hundred KB on disk and loads in
/// under 50ms.
/// </para>
///
/// <para>
/// File format (little-endian):
/// <code>
///   Magic     : 4 bytes  "SRTX"
///   Version   : 4 bytes  int
///   DumpLength: 8 bytes  long
///   DumpTicks : 8 bytes  long
///   Count     : 4 bytes  int
///   Addresses : Count × 8 bytes  ulong  (unsorted — insertion order)
/// </code>
/// </para>
/// </summary>
public static class StaticRootsCache
{
    private static readonly byte[] MagicBytes = "SRTX"u8.ToArray();
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "static-roots.bin");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    public static bool IsValid(string cachePath, string dumpPath)
    {
        if (!File.Exists(cachePath) || !File.Exists(dumpPath)) return false;
        try
        {
            var dumpInfo = new FileInfo(dumpPath);
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize: 512);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4) return false;
            for (int i = 0; i < 4; i++)
                if (magic[i] != MagicBytes[i]) return false;

            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            if (br.ReadInt32() != CurrentVersion)                  return false;
            if (br.ReadInt64() != dumpInfo.Length)                 return false;
            if (br.ReadInt64() != dumpInfo.LastWriteTimeUtc.Ticks) return false;
            return true;
        }
        catch { return false; }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    public static void Save(string cachePath, string dumpPath, HashSet<ulong> addresses)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                          FileShare.None, bufferSize: 256 * 1024);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            fs.Write(MagicBytes);
            bw.Write(CurrentVersion);
            bw.Write(dumpInfo.Length);
            bw.Write(dumpInfo.LastWriteTimeUtc.Ticks);
            bw.Write(addresses.Count);

            // Write as flat ulong span — no per-element overhead.
            const int bufSize = 4096; // 32 KB
            Span<byte> buf    = stackalloc byte[bufSize * 8];
            int pos = 0;
            foreach (ulong addr in addresses)
            {
                MemoryMarshal.Write(buf.Slice(pos * 8, 8), in addr);
                pos++;
                if (pos == bufSize)
                {
                    fs.Write(buf.Slice(0, pos * 8));
                    pos = 0;
                }
            }
            if (pos > 0)
                fs.Write(buf.Slice(0, pos * 8));
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
    /// Streams the cache file into a <see cref="HashSet{T}"/>.
    /// Returns <see langword="null"/> on any read error.
    /// </summary>
    public static HashSet<ulong>? TryLoad(string cachePath)
    {
        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read, bufferSize: 256 * 1024);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4) return null;
            for (int i = 0; i < 4; i++)
                if (magic[i] != MagicBytes[i]) return null;

            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            if (br.ReadInt32() != CurrentVersion) return null;
            br.ReadInt64(); // DumpLength
            br.ReadInt64(); // DumpTicks

            int count  = br.ReadInt32();
            var result = new HashSet<ulong>(count);

            // Read in bulk using a stack buffer.
            const int bufAddrs = 4096;
            Span<byte> buf = stackalloc byte[bufAddrs * 8];
            int remaining  = count;
            while (remaining > 0)
            {
                int batch = Math.Min(remaining, bufAddrs);
                var slice = buf.Slice(0, batch * 8);
                int read  = fs.ReadAtLeast(slice, slice.Length, throwOnEndOfStream: false);
                if (read < slice.Length) break; // truncated
                var ulongSpan = MemoryMarshal.Cast<byte, ulong>(slice);
                for (int i = 0; i < batch; i++)
                    result.Add(ulongSpan[i]);
                remaining -= batch;
            }
            return result;
        }
        catch { return null; }
    }
}
