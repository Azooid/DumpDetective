using System.Text;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists the GC root map produced by <see cref="Analyzers.MemoryLeakAnalyzer"/>
/// Step 4a to a small binary file in <c>.ddcache\&lt;dump-name&gt;\</c>.
///
/// <para>
/// <c>ClrHeap.EnumerateRoots()</c> reads deeply from the dump and typically takes
/// 250–300s on a large heap. The cached file is typically 5–30 MB and loads in &lt;1s.
/// </para>
///
/// <para>
/// File format (little-endian, BinaryWriter strings = 7-bit length-prefixed UTF-8):
/// <code>
///   Magic     : 4 bytes  "GCR0"
///   Version   : 4 bytes  int
///   DumpLength: 8 bytes  long
///   DumpTicks : 8 bytes  long
///   KindCount : 2 bytes  ushort   — number of unique Kind strings (≈10–20)
///   Per kind  : BinaryWriter.Write(string)
///   Count     : 4 bytes  int      — total root entries
///   Per entry :
///     Addr    : 8 bytes  ulong
///     KindIdx : 1 byte   byte     — index into kinds array above
///     HasType : 1 byte   bool
///     ObjType : BinaryWriter.Write(string) — only present when HasType = true
/// </code>
/// </para>
/// </summary>
public static class GcRootsCache
{
    private static readonly byte[] MagicBytes = "GCR0"u8.ToArray();
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "gc-roots.bin");
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

    /// <summary>
    /// Writes the root map to <paramref name="cachePath"/> using an atomic temp-write.
    /// </summary>
    public static void Save(
        string cachePath,
        string dumpPath,
        Dictionary<ulong, (string Kind, string? ObjType)> roots)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";

        // Build unique kind table for compact storage.
        var kindTable  = new List<string>(16);
        var kindIndex  = new Dictionary<string, byte>(16, StringComparer.Ordinal);
        foreach (var (_, (kind, _)) in roots)
        {
            if (!kindIndex.ContainsKey(kind))
            {
                kindIndex[kind] = (byte)kindTable.Count;
                kindTable.Add(kind);
            }
        }

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

            bw.Write((ushort)kindTable.Count);
            foreach (var k in kindTable)
                bw.Write(k);

            bw.Write(roots.Count);
            foreach (var (addr, (kind, objType)) in roots)
            {
                bw.Write(addr);
                bw.Write(kindIndex[kind]);
                bool hasType = objType is not null;
                bw.Write(hasType);
                if (hasType)
                    bw.Write(objType!);
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
    /// Streams the cache file and rebuilds the root dictionary.
    /// Returns <see langword="null"/> on any read error.
    /// </summary>
    public static Dictionary<ulong, (string Kind, string? ObjType)>? TryLoad(string cachePath)
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
            br.ReadInt64(); // DumpLength (already validated by IsValid)
            br.ReadInt64(); // DumpTicks

            int kindCount = br.ReadUInt16();
            var kinds     = new string[kindCount];
            for (int i = 0; i < kindCount; i++)
                kinds[i] = string.Intern(br.ReadString());

            int count = br.ReadInt32();
            var result = new Dictionary<ulong, (string Kind, string? ObjType)>(count);
            for (int i = 0; i < count; i++)
            {
                ulong  addr    = br.ReadUInt64();
                string kind    = kinds[br.ReadByte()];
                string? type   = br.ReadBoolean() ? br.ReadString() : null;
                result[addr]   = (kind, type);
            }
            return result;
        }
        catch { return null; }
    }
}
