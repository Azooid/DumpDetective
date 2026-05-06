using System.Text;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Persists an <see cref="EventAnalysisData"/> result to a binary file in
/// <c>.ddcache\&lt;dump-name&gt;\</c> so subsequent <c>event-analysis</c> runs
/// and <c>analyze --full</c> can skip the expensive heap walk (~60–200s).
///
/// <para>
/// File format (little-endian, BinaryWriter strings = 7-bit length-prefixed UTF-8):
/// <code>
///   Magic               : 4 bytes  "EVTA"
///   Version             : 4 bytes  int
///   DumpLength          : 8 bytes  long
///   DumpTicks           : 8 bytes  long
///   PublisherInstCount  : 4 bytes  int
///   GroupCount          : 4 bytes  int
///   Per group           :
///     Publisher         : string
///     Field             : string
///     Subscribers       : 4 bytes  int
///     IsStaticPublisher : 1 byte   bool
///     HasStaticSubs     : 1 byte   bool
///     DuplicateCount    : 4 bytes  int
///     RetainedBytes     : 8 bytes  long
///     LambdaCount       : 4 bytes  int
///     InstanceCount     : 4 bytes  int
///     SubCount          : 4 bytes  int
///     Per subscriber    :
///       TargetType      : string
///       MethodName      : string
///       Size            : 8 bytes  long
///       IsStaticRooted  : 1 byte   bool
///       IsLambda        : 1 byte   bool
///       TargetAddr      : 8 bytes  ulong
/// </code>
/// </para>
/// </summary>
public static class EventAnalysisCache
{
    private static readonly byte[] MagicBytes = "EVTA"u8.ToArray();
    private const int CurrentVersion = 1;

    // ── Path ─────────────────────────────────────────────────────────────────

    public static string CachePath(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        return Path.Combine(dumpDir, ".ddcache", dumpName, "event-analysis.bin");
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

    public static void Save(string cachePath, string dumpPath, EventAnalysisData data)
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

            bw.Write(data.PublisherInstanceCount);
            bw.Write(data.Groups.Count);

            foreach (var g in data.Groups)
            {
                bw.Write(g.Publisher);
                bw.Write(g.Field);
                bw.Write(g.Subscribers);
                bw.Write(g.IsStaticPublisher);
                bw.Write(g.HasStaticSubs);
                bw.Write(g.DuplicateCount);
                bw.Write(g.RetainedBytes);
                bw.Write(g.LambdaCount);
                bw.Write(g.InstanceCount);

                var subs = g.AllSubs ?? [];
                bw.Write(subs.Count);
                foreach (var s in subs)
                {
                    bw.Write(s.TargetType);
                    bw.Write(s.MethodName);
                    bw.Write(s.Size);
                    bw.Write(s.IsStaticRooted);
                    bw.Write(s.IsLambda);
                    bw.Write(s.TargetAddr);
                }
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

    public static EventAnalysisData? TryLoad(string cachePath, string dumpPath)
    {
        if (!IsValid(cachePath, dumpPath)) return null;
        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read, bufferSize: 256 * 1024);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            // Skip magic (4) + version (4) + dumpLength (8) + dumpTicks (8) — already validated
            fs.Seek(24, SeekOrigin.Begin);

            int publisherInstCount = br.ReadInt32();
            int groupCount         = br.ReadInt32();

            var groups = new List<EventLeakGroup>(groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                string publisher         = br.ReadString();
                string field             = br.ReadString();
                int    subscribers       = br.ReadInt32();
                bool   isStaticPublisher = br.ReadBoolean();
                bool   hasStaticSubs     = br.ReadBoolean();
                int    duplicateCount    = br.ReadInt32();
                long   retainedBytes     = br.ReadInt64();
                int    lambdaCount       = br.ReadInt32();
                int    instanceCount     = br.ReadInt32();

                int subCount = br.ReadInt32();
                var subs     = new List<EventSubscriberInfo>(subCount);
                for (int j = 0; j < subCount; j++)
                {
                    string targetType    = br.ReadString();
                    string methodName    = br.ReadString();
                    long   size          = br.ReadInt64();
                    bool   isStaticRoot  = br.ReadBoolean();
                    bool   isLambda      = br.ReadBoolean();
                    ulong  targetAddr    = br.ReadUInt64();
                    subs.Add(new EventSubscriberInfo(targetType, methodName, size, isStaticRoot, isLambda, targetAddr));
                }

                groups.Add(new EventLeakGroup(publisher, field, subscribers,
                    isStaticPublisher, hasStaticSubs, duplicateCount,
                    retainedBytes, lambdaCount, instanceCount, subs));
            }

            return new EventAnalysisData(groups, publisherInstCount);
        }
        catch { return null; }
    }
}
