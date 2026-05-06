using System.Runtime.InteropServices;
using System.Text;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists the HotAddrTypes result of <see cref="Analyzers.SharedReferrerCache.BuildHotTypesFromBfs"/>
/// to disk so that the expensive CSR edge scan (~170s on 87M-node graphs) is only performed once.
///
/// <para>
/// File format (little-endian, BinaryWriter strings = 7-bit length-prefixed UTF-8):
/// <code>
///   Magic       : 4 bytes  "HATP"
///   Version     : 4 bytes  int
///   DumpLength  : 8 bytes  long
///   DumpTicks   : 8 bytes  long
///   AddrCount   : 4 bytes  int     — number of hot addresses
///   Per address :
///     Addr      : 8 bytes  ulong
///     TypeCount : 4 bytes  int
///     Per type  :
///       TypeName: BinaryWriter.Write(string)
///       Count   : 4 bytes  int
/// </code>
/// </para>
/// </summary>
public static class HotAddrTypesCache
{
    private static readonly byte[] MagicBytes = "HATP"u8.ToArray();
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "hot-addr-types.bin");
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

    public static void Save(
        string cachePath,
        string dumpPath,
        Dictionary<ulong, Dictionary<string, int>> data)
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
            bw.Write(data.Count);

            foreach (var (addr, typeMap) in data)
            {
                bw.Write(addr);
                bw.Write(typeMap.Count);
                foreach (var (typeName, count) in typeMap)
                {
                    bw.Write(typeName);
                    bw.Write(count);
                }
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

    public static Dictionary<ulong, Dictionary<string, int>>? TryLoad(string cachePath)
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

            int addrCount = br.ReadInt32();
            var result    = new Dictionary<ulong, Dictionary<string, int>>(addrCount);
            for (int i = 0; i < addrCount; i++)
            {
                ulong addr      = br.ReadUInt64();
                int   typeCount = br.ReadInt32();
                var   typeMap   = new Dictionary<string, int>(typeCount, StringComparer.Ordinal);
                for (int t = 0; t < typeCount; t++)
                {
                    string typeName = br.ReadString();
                    int    count    = br.ReadInt32();
                    typeMap[typeName] = count;
                }
                result[addr] = typeMap;
            }
            return result;
        }
        catch { return null; }
    }
}
