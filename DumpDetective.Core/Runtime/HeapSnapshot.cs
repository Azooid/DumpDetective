using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace DumpDetective.Core.Runtime;

/// <summary>
/// Result of a single shared heap walk built once per analysis session and
/// reused across all sub-commands, eliminating redundant <c>EnumerateObjects</c> calls.
/// <para>
/// Memory strategy (balancing speed vs. RAM):
/// <list type="bullet">
///   <item><b>TypeStats</b> — kept in memory (~2–20 MB). Small, read by several analyzers;
///   disk I/O would add overhead with no meaningful saving.</item>
///   <item><b>InboundCounts</b> — NOT stored here at all. The raw ~1.9 GB
///   <c>Dictionary&lt;ulong,int&gt;</c> lives only in <c>InboundRefConsumer</c> and is freed
///   immediately after the heap walk via <c>InboundRefConsumer.ReleaseRaw()</c>.
///   The pre-distilled summaries (<c>TopInboundAddrs</c>, <c>InboundHistogram</c>) are kept
///   in memory as tiny arrays.</item>
///   <item><b>StringGroups</b> — written to a binary cache file under
///   <c>&lt;dump-dir&gt;\.ddcache\&lt;dump-name&gt;\</c> and streamed back on demand.
///   Saves ~200 MB–1 GB depending on heap string diversity; read only once by
///   <c>StringDuplicatesAnalyzer</c>, then the file is deleted.</item>
/// </list>
/// Call <c>Dispose()</c> (or rely on <c>DumpContext.Dispose()</c>) to clean up the cache
/// directory when the analysis session ends.
/// </para>
/// </summary>
internal sealed class HeapSnapshot : IDisposable
{
    // ── In-memory TypeStats (small; ~2–20 MB; released explicitly) ───────────
    private Dictionary<string, TypeAgg>? _typeStats;
    private int _typeStatsReaders;  // reference count — released when it drops to 0

    // ── StringGroups disk cache ───────────────────────────────────────────────
    private string? _tempDir;
    private string? _stringGroupsPath;

    // ── Entry counts ─────────────────────────────────────────────────────────
    internal int  TypeStatsCount    { get; private set; }
    internal int  StringGroupsCount { get; private set; }

    // ── Pre-distilled inbound-ref summaries (always kept in memory) ───────────
    internal HeapAddrCount[] TopInboundAddrs  { get; private set; } = [];
    internal InboundBucket[] InboundHistogram { get; private set; } = [];
    internal int             InboundCountsSize { get; private set; }

    // ── Generation byte totals ────────────────────────────────────────────────
    internal long Gen0Total { get; }
    internal long Gen1Total { get; }
    internal long Gen2Total { get; }
    internal long LohTotal  { get; }
    internal long PohTotal  { get; }

    // ── Generation object counts ──────────────────────────────────────────────
    internal long Gen0ObjCount { get; }
    internal long Gen1ObjCount { get; }
    internal long Gen2ObjCount { get; }

    // ── Frozen / POH detail ───────────────────────────────────────────────────
    internal long FrozenObjCount { get; }
    internal long FrozenObjSize  { get; }
    internal long PohObjCount    { get; }
    internal long PohObjSize     { get; }

    // ── Totals ────────────────────────────────────────────────────────────────
    internal long TotalObjects     { get; }
    internal long TotalRefs        { get; }
    internal long TotalStringCount { get; }
    internal long TotalStringSize  { get; }

    private const int FileBufferSize = 512 * 1024; // 512 KB — used for StringGroups disk I/O

    private HeapSnapshot(
        long gen0, long gen1, long gen2, long loh, long poh,
        long gen0c, long gen1c, long gen2c,
        long frozenObjCount, long frozenObjSize,
        long pohObjCount, long pohObjSize,
        long totalObjs, long totalRefs,
        long totalStringCount, long totalStringSize,
        HeapAddrCount[] topInboundAddrs,
        InboundBucket[] inboundHistogram,
        int             inboundCountsSize)
    {
        Gen0Total         = gen0;  Gen1Total = gen1;  Gen2Total = gen2;
        LohTotal          = loh;   PohTotal  = poh;
        Gen0ObjCount      = gen0c; Gen1ObjCount = gen1c; Gen2ObjCount = gen2c;
        FrozenObjCount    = frozenObjCount; FrozenObjSize = frozenObjSize;
        PohObjCount       = pohObjCount;    PohObjSize    = pohObjSize;
        TotalObjects      = totalObjs;      TotalRefs     = totalRefs;
        TotalStringCount  = totalStringCount; TotalStringSize = totalStringSize;
        TopInboundAddrs   = topInboundAddrs;
        InboundHistogram  = inboundHistogram;
        InboundCountsSize = inboundCountsSize;
    }

    // ── TypeStats access (in-memory) ──────────────────────────────────────────

    /// <summary>
    /// Iterates per-type statistics. When the in-memory dictionary is still alive
    /// this is a direct enumeration (no I/O, same speed as before). Returns an empty
    /// sequence after <see cref="ReleaseTypeStats"/> has been called.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, TypeAgg>> StreamTypeStats()
    {
        if (_typeStats is null) yield break;
        foreach (var kv in _typeStats)
            yield return kv;
    }

    // ── StringGroups streaming (disk-backed) ─────────────────────────────────

    /// <summary>
    /// Streams per-string-value duplicate statistics from the disk cache file.
    /// Returns an empty sequence when the file has been released or is unavailable.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, StringGroupStats>> StreamStringGroups()
    {
        if (_stringGroupsPath is null || !File.Exists(_stringGroupsPath)) yield break;
        using var fs = new FileStream(_stringGroupsPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, FileBufferSize, FileOptions.SequentialScan);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
        int count = br.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string key  = br.ReadString();
            int    cnt  = br.ReadInt32();
            long   size = br.ReadInt64();
            yield return new KeyValuePair<string, StringGroupStats>(key, new StringGroupStats(cnt, size));
        }
    }

    // ── Release methods ───────────────────────────────────────────────────────

    /// <summary>
    /// No-op — InboundCounts are not stored in the snapshot; they are freed by the
    /// consumer immediately after the heap walk. This method exists so call sites
    /// in <c>SharedReferrerCache.Build</c> remain valid.
    /// </summary>
    internal void ReleaseInboundCounts() { }

    /// <summary>Releases the in-memory reference to the StringGroups cache file. The file itself is kept on disk for reuse.</summary>
    internal void ReleaseStringGroups()
    {
        _stringGroupsPath = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    /// <summary>
    /// Declares that one more caller will read TypeStats. Each call must be balanced
    /// by exactly one <see cref="RetireTypeStatsReader"/> call.
    /// Must be called before the parallel walk starts (i.e. on the collection thread).
    /// </summary>
    internal void RegisterTypeStatsReader()
        => Interlocked.Increment(ref _typeStatsReaders);

    /// <summary>
    /// Signals that one reader has finished. When the count reaches zero the
    /// dictionary is cleared and released for GC.
    /// </summary>
    internal void RetireTypeStatsReader()
    {
        if (Interlocked.Decrement(ref _typeStatsReaders) == 0)
            ReleaseTypeStats();
    }

    /// <summary>Releases the in-memory TypeStats dictionary immediately (bypass ref count).</summary>
    internal void ReleaseTypeStats()
    {
        if (_typeStats is not null)
        {
            _typeStats.Clear();
            _typeStats.TrimExcess();
            _typeStats = null;
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    /// <summary>Releases in-memory references. The .ddcache directory and all files in it are kept on disk for reuse by subsequent runs.</summary>
    public void Dispose()
    {
        _typeStats         = null;
        _stringGroupsPath  = null;
        _tempDir           = null;  // keep the directory; close command handles cleanup
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Factory used by consumers after a <c>HeapWalker</c> walk.
    /// <list type="bullet">
    ///   <item><c>typeStats</c> is stored in memory (small).</item>
    ///   <item><c>inboundCounts</c> is not stored — the caller must free it via
    ///   <c>InboundRefConsumer.ReleaseRaw()</c> immediately after this call.</item>
    ///   <item><c>stringGroups</c> is written to
    ///   <c>&lt;dump-dir&gt;\.ddcache\&lt;dump-name&gt;\stringGroups.bin</c>.</item>
    /// </list>
    /// </summary>
    internal static HeapSnapshot Create(
        Dictionary<string, TypeAgg> typeStats,
        Dictionary<ulong, int> inboundCounts,
        Dictionary<string, StringGroupStats> stringGroups,
        long gen0, long gen1, long gen2, long loh, long poh,
        long gen0c, long gen1c, long gen2c,
        long frozenObjCount, long frozenObjSize,
        long pohObjCount, long pohObjSize,
        long totalObjs, long totalRefs,
        long totalStringCount, long totalStringSize,
        HeapAddrCount[]   topInboundAddrs,
        InboundBucket[]   inboundHistogram,
        int               inboundCountsSize,
        string            dumpPath = "")
    {
        var snap = new HeapSnapshot(
            gen0, gen1, gen2, loh, poh,
            gen0c, gen1c, gen2c,
            frozenObjCount, frozenObjSize,
            pohObjCount, pohObjSize,
            totalObjs, totalRefs,
            totalStringCount, totalStringSize,
            topInboundAddrs, inboundHistogram, inboundCountsSize);

        // TypeStats — keep in memory; small (~2–20 MB), fast to iterate, used by 3 analyzers.
        snap._typeStats     = typeStats;
        snap.TypeStatsCount = typeStats.Count;

        // InboundCounts — not stored; caller frees the consumer dict via ReleaseRaw().

        // StringGroups — disk-backed to save ~200 MB–1 GB during the analysis phase.
        snap.StringGroupsCount = stringGroups.Count;
        if (stringGroups.Count > 0)
        {
            snap._tempDir          = ResolveCacheDir(dumpPath);
            Directory.CreateDirectory(snap._tempDir);
            snap._stringGroupsPath = Path.Combine(snap._tempDir, "stringGroups.bin");
            WriteStringGroups(snap._stringGroupsPath, stringGroups);
        }

        return snap;
    }

    // ── Private write helpers ─────────────────────────────────────────────────

    private static void WriteStringGroups(string path, Dictionary<string, StringGroupStats> data)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, FileBufferSize);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
        bw.Write(data.Count);
        foreach (var (key, stats) in data)
        {
            bw.Write(key);
            bw.Write(stats.Count);
            bw.Write(stats.TotalSize);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the cache directory: <c>&lt;dump-dir&gt;\.ddcache\&lt;dump-name-no-ext&gt;\</c>.
    /// Falls back to a uniquely-named folder in <see cref="Path.GetTempPath"/> when the
    /// dump path is empty or the directory cannot be determined.
    /// </summary>
    private static string ResolveCacheDir(string dumpPath)
    {
        if (!string.IsNullOrEmpty(dumpPath))
        {
            try
            {
                string dumpDir  = Path.GetDirectoryName(Path.GetFullPath(dumpPath)) ?? Path.GetTempPath();
                string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
                return Path.Combine(dumpDir, ".ddcache", dumpName);
            }
            catch { /* fall through */ }
        }
        return Path.Combine(Path.GetTempPath(), $"dd-snap-{Guid.NewGuid():N}");
    }

    private static void TryDeleteFile(ref string? path)
    {
        if (path is null) return;
        var p = path;
        path  = null;
        try { File.Delete(p); } catch { }
    }

    // ── Standalone build ──────────────────────────────────────────────────────

    /// <summary>
    /// Standalone build — walks the heap once when no pre-built snapshot is available.
    /// Called by <see cref="DumpContext.EnsureSnapshot"/>.
    /// Writes the large collections to temp files via <see cref="Create"/> at the end.
    /// </summary>
    internal static HeapSnapshot Build(DumpContext ctx)
    {
        var typeStats     = new Dictionary<string, TypeAgg>(2048, StringComparer.Ordinal);
        var inboundCounts = new Dictionary<ulong, int>(65536);
        var stringGroups  = new Dictionary<string, StringGroupStats>(StringComparer.Ordinal);

        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0;
        long gen0c = 0, gen1c = 0, gen2c = 0;
        long frozenObjCount = 0, frozenObjSize = 0;
        long pohObjCount = 0, pohObjSize = 0;
        long totalObjs = 0, totalRefs = 0;
        long totalStringCount = 0, totalStringSize = 0;

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;

            string name = obj.Type.Name ?? "<unknown>";
            long   size = (long)obj.Size;
            var    seg  = ctx.Heap.GetSegmentByAddress(obj.Address);

            bool g0 = false, g1 = false, g2 = false, isL = false, isP = false, isFrozen = false;
            switch (seg?.Kind)
            {
                case GCSegmentKind.Generation0: g0 = true;  break;
                case GCSegmentKind.Generation1: g1 = true;  break;
                case GCSegmentKind.Generation2: g2 = true;  break;
                case GCSegmentKind.Large:       isL = true; break;
                case GCSegmentKind.Pinned:      isP = true; break;
                case GCSegmentKind.Frozen:      isFrozen = true; break;
                case GCSegmentKind.Ephemeral when seg is not null:
                    if      (seg.Generation0.Contains(obj.Address)) g0 = true;
                    else if (seg.Generation1.Contains(obj.Address)) g1 = true;
                    else                                             g2 = true;
                    break;
                default: g2 = true; break;
            }

            if (!typeStats.TryGetValue(name, out var acc))
            {
                acc = new TypeAgg { Name = name, MT = obj.Type.MethodTable,
                    GenLabel = g0 ? "Gen0" : g1 ? "Gen1" : g2 ? "Gen2" :
                               isL ? "LOH" : isP ? "POH" : isFrozen ? "Frozen" : "Gen2" };
                typeStats[name] = acc;
            }
            acc.Count++; acc.Size += size;
            if (g0)  { acc.G0c++; acc.G0s += size; }
            if (g1)  { acc.G1c++; acc.G1s += size; }
            if (g2)  { acc.G2c++; acc.G2s += size; }
            if (isL) { acc.Lc++;  acc.Ls  += size; }
            if (isP) { acc.Pc++;  acc.Ps  += size; }
            if (acc.SampleAddrs.Count < 5) acc.SampleAddrs.Add(obj.Address);

            if (g0)      { gen0 += size; gen0c++; }
            if (g1)      { gen1 += size; gen1c++; }
            if (g2)      { gen2 += size; gen2c++; }
            if (isL)       loh  += size;
            if (isP)     { poh  += size; pohObjCount++; pohObjSize += size; }
            if (isFrozen){ frozenObjCount++; frozenObjSize += size; }
            totalObjs++;

            try
            {
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(inboundCounts, refAddr, out _);
                    c++;
                    totalRefs++;
                }
            }
            catch { /* skip objects with unreadable reference fields */ }

            if (name == "System.String")
            {
                totalStringCount++;
                totalStringSize += size;
                try
                {
                    var val = obj.AsString(maxLength: 512) ?? string.Empty;
                    ref var sg = ref CollectionsMarshal.GetValueRefOrAddDefault(stringGroups, val, out bool sgExisted);
                    if (sgExisted) sg = new StringGroupStats(sg.Count + 1, sg.TotalSize + size);
                    else           sg = new StringGroupStats(1, size);
                }
                catch { /* skip corrupted string objects */ }
            }
        }

        return Create(
            typeStats, inboundCounts, stringGroups,
            gen0, gen1, gen2, loh, poh,
            gen0c, gen1c, gen2c,
            frozenObjCount, frozenObjSize,
            pohObjCount, pohObjSize,
            totalObjs, totalRefs,
            totalStringCount, totalStringSize,
            topInboundAddrs:   [],
            inboundHistogram:  [],
            inboundCountsSize: inboundCounts.Count,
            dumpPath:          ctx.DumpPath);
    }
}

/// <summary>Per-type statistics accumulated during a heap walk.</summary>
internal sealed class TypeAgg
{
    public string Name     = "";
    public ulong  MT;
    public long   Count, Size;
    public long   G0c, G0s;
    public long   G1c, G1s;
    public long   G2c, G2s;
    public long   Lc,  Ls;
    public long   Pc,  Ps;
    public string GenLabel = "Gen2";
    public readonly List<ulong> SampleAddrs = new(5);
}
