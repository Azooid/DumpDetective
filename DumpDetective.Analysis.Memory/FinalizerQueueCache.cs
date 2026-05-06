using System.Text;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists a <see cref="FinalizerQueueData"/> result to a small binary file in
/// <c>.ddcache\&lt;dump-name&gt;\</c> so subsequent <c>finalizer-queue</c> runs
/// and <c>analyze --full</c> can skip the expensive <c>EnumerateFinalizableObjects</c>
/// + <c>EnumerateHandles</c> enumeration (typically 5–30s).
///
/// <para>
/// File format (little-endian, BinaryWriter strings = 7-bit length-prefixed UTF-8):
/// <code>
///   Magic           : 4 bytes  "FINQ"
///   Version         : 4 bytes  int
///   DumpLength      : 8 bytes  long
///   DumpTicks       : 8 bytes  long
///   FinalizerBlocked: 1 byte   bool
///   ThreadId        : 4 bytes  int
///   ThreadOsId      : 4 bytes  uint
///   ResurrectionCnt : 4 bytes  int
///   FrameCount      : 4 bytes  int
///   Per frame       : BinaryWriter.Write(string)
///   StatCount       : 4 bytes  int
///   Per stat        : TypeName(string) + Count(4) + Size(8) + Gen0/1/2/Loh/Poh(4 each)
///                     HasDispose(1) + IsCritical(1) + AddrCount(4) + Addrs(8 each)
/// </code>
/// </para>
/// </summary>
public static class FinalizerQueueCache
{
    private static readonly byte[] MagicBytes = "FINQ"u8.ToArray();
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "finalizer-queue.bin");
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

    public static void Save(string cachePath, string dumpPath, FinalizerQueueData data)
    {
        var    dumpInfo = new FileInfo(dumpPath);
        string tmp      = cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                          FileShare.None, bufferSize: 256 * 1024);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            // Header
            bw.Write(MagicBytes);
            bw.Write(CurrentVersion);
            bw.Write(dumpInfo.Length);
            bw.Write(dumpInfo.LastWriteTimeUtc.Ticks);

            // Thread info
            bw.Write(data.FinalizerThreadBlocked);
            bw.Write(data.FinalizerThreadId);
            bw.Write(data.FinalizerThreadOSId);
            bw.Write(data.ResurrectionCount);

            // Frames
            bw.Write(data.FinalizerFrames.Count);
            foreach (var frame in data.FinalizerFrames)
                bw.Write(frame);

            // Per-type stats
            bw.Write(data.Stats.Count);
            foreach (var (typeName, stat) in data.Stats)
            {
                bw.Write(typeName);
                bw.Write(stat.Count);
                bw.Write(stat.Size);
                bw.Write(stat.Gen0);
                bw.Write(stat.Gen1);
                bw.Write(stat.Gen2);
                bw.Write(stat.Loh);
                bw.Write(stat.Poh);
                bw.Write(stat.HasDispose);
                bw.Write(stat.IsCritical);
                bw.Write(stat.Addresses.Count);
                foreach (var addr in stat.Addresses)
                    bw.Write(addr);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        try { File.Move(tmp, cachePath, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    public static FinalizerQueueData? TryLoad(string cachePath, string dumpPath)
    {
        if (!IsValid(cachePath, dumpPath)) return null;
        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read, bufferSize: 256 * 1024);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            // Skip magic (4) + version (4) + dumpLength (8) + dumpTicks (8) — already validated
            fs.Seek(24, SeekOrigin.Begin);

            bool   finBlocked   = br.ReadBoolean();
            int    threadId     = br.ReadInt32();
            uint   threadOsId   = br.ReadUInt32();
            int    resurrection = br.ReadInt32();

            int frameCount = br.ReadInt32();
            var frames     = new List<string>(frameCount);
            for (int i = 0; i < frameCount; i++)
                frames.Add(br.ReadString());

            int statCount = br.ReadInt32();
            var stats     = new Dictionary<string, FinalizerTypeStats>(statCount, StringComparer.Ordinal);
            int total     = 0;
            long totalSize = 0;
            for (int i = 0; i < statCount; i++)
            {
                string typeName    = br.ReadString();
                int    count       = br.ReadInt32();
                long   size        = br.ReadInt64();
                int    gen0        = br.ReadInt32();
                int    gen1        = br.ReadInt32();
                int    gen2        = br.ReadInt32();
                int    loh         = br.ReadInt32();
                int    poh         = br.ReadInt32();
                bool   hasDispose  = br.ReadBoolean();
                bool   isCritical  = br.ReadBoolean();
                int    addrCount   = br.ReadInt32();
                var    addresses   = new List<ulong>(addrCount);
                for (int a = 0; a < addrCount; a++)
                    addresses.Add(br.ReadUInt64());

                stats[typeName] = new FinalizerTypeStats(count, size, gen0, gen1, gen2, loh, poh,
                                                         hasDispose, isCritical, addresses);
                total     += count;
                totalSize += size;
            }

            return new FinalizerQueueData(stats, total, totalSize, finBlocked, frames, resurrection,
                                          threadId, threadOsId);
        }
        catch { return null; }
    }
}
