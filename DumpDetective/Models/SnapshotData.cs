using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Models;

// ── Named pair type (replaces ValueTuple which can't be AOT-serialized) ───────

public sealed class NameCount
{
    public string Name  { get; set; } = string.Empty;
    public int    Count { get; set; }

    // Parameterless ctor for deserialization
    public NameCount() { }
    public NameCount(string name, int count) { Name = name; Count = count; }
}

// ── Serialisable snapshot DTO ─────────────────────────────────────────────────

/// <summary>
/// A fully serialisable mirror of <see cref="DumpSnapshot"/>.
/// ValueTuple list fields are replaced by <see cref="NameCount"/> lists,
/// and computed-only properties (<c>Label</c>, <c>LohFreeBytes</c>, …)
/// are omitted — they are derived when converting back.
/// </summary>
public sealed class SnapshotData
{
    // ── Identity ─────────────────────────────────────────────────────────────
    public string   DumpPath          { get; set; } = string.Empty;
    public long     DumpFileSizeBytes { get; set; }
    public DateTime FileTime          { get; set; }
    public string?  ClrVersion        { get; set; }
    public bool     IsFullMode        { get; set; }

    // ── Memory ───────────────────────────────────────────────────────────────
    public long   TotalHeapBytes   { get; set; }
    public long   Gen0Bytes        { get; set; }
    public long   Gen1Bytes        { get; set; }
    public long   Gen2Bytes        { get; set; }
    public long   LohBytes         { get; set; }
    public long   LohLiveBytes     { get; set; }
    public long   PohBytes         { get; set; }
    public long   FrozenBytes      { get; set; }
    public double FragmentationPct { get; set; }
    public long   HeapFreeBytes    { get; set; }

    // ── Object counts ────────────────────────────────────────────────────────
    public long TotalObjectCount { get; set; }
    public long LohObjectCount   { get; set; }

    // ── Top types ────────────────────────────────────────────────────────────
    public List<TypeStat> TopTypes { get; set; } = [];

    // ── Exceptions ───────────────────────────────────────────────────────────
    public List<NameCount> ExceptionCounts { get; set; } = [];

    // ── Async ────────────────────────────────────────────────────────────────
    public int             AsyncBacklogTotal { get; set; }
    public List<NameCount> TopAsyncMethods   { get; set; } = [];

    // ── Timers / WCF / DB ────────────────────────────────────────────────────
    public int TimerCount      { get; set; }
    public int WcfObjectCount  { get; set; }
    public int WcfFaultedCount { get; set; }
    public int ConnectionCount { get; set; }

    // ── GC handles ───────────────────────────────────────────────────────────
    public int PinnedHandleCount { get; set; }
    public int WeakHandleCount   { get; set; }
    public int StrongHandleCount { get; set; }
    public int TotalHandleCount  { get; set; }

    // ── Finalizer queue ──────────────────────────────────────────────────────
    public int             FinalizerQueueDepth { get; set; }
    public List<NameCount> TopFinalizerTypes   { get; set; } = [];

    // ── Threads ──────────────────────────────────────────────────────────────
    public int ThreadCount          { get; set; }
    public int AliveThreadCount     { get; set; }
    public int BlockedThreadCount   { get; set; }
    public int ExceptionThreadCount { get; set; }

    // ── Thread pool ──────────────────────────────────────────────────────────
    public int TpMinWorkers    { get; set; }
    public int TpMaxWorkers    { get; set; }
    public int TpActiveWorkers { get; set; }
    public int TpIdleWorkers   { get; set; }

    // ── Event leaks ──────────────────────────────────────────────────────────
    public int                 EventLeakFieldCount  { get; set; }
    public int                 EventSubscriberTotal { get; set; }
    public int                 EventLeakMaxOnField  { get; set; }
    public List<EventLeakStat> TopEventLeaks        { get; set; } = [];

    // ── String duplicates ────────────────────────────────────────────────────
    public long                      StringTotalBytes      { get; set; }
    public long                      StringWastedBytes     { get; set; }
    public int                       StringDuplicateGroups { get; set; }
    public int                       UniqueStringCount     { get; set; }
    public List<StringDuplicateStat> TopStringDuplicates   { get; set; } = [];

    // ── Rooted objects ───────────────────────────────────────────────────────
    public List<RootedHandleStat> TopRootedTypes { get; set; } = [];

    // ── Modules ──────────────────────────────────────────────────────────────
    public int ModuleCount    { get; set; }
    public int AppModuleCount { get; set; }

    // ── Findings + score ─────────────────────────────────────────────────────
    public List<Finding> Findings    { get; set; } = [];
    public int           HealthScore { get; set; } = 100;

    // ── Captured sub-reports (optional — populated by trend-analysis --full --output *.json) ──
    /// <summary>
    /// Captured render output from <c>AnalyzeCommand.RenderReport</c> +
    /// <c>AnalyzeCommand.RenderEmbeddedReports</c> for this dump, stored so
    /// <c>trend-render</c> can replay all per-dump sub-reports without reopening the dump file.
    /// Null when the snapshot was saved without <c>--full</c>.
    /// </summary>
    public ReportDoc? SubReport { get; set; }

    // ── Conversion ───────────────────────────────────────────────────────────

    public static SnapshotData From(DumpSnapshot s, ReportDoc? subReport = null) => new()
    {
        DumpPath          = s.DumpPath,
        DumpFileSizeBytes = s.DumpFileSizeBytes,
        FileTime          = s.FileTime,
        ClrVersion        = s.ClrVersion,
        IsFullMode        = s.IsFullMode,
        TotalHeapBytes    = s.TotalHeapBytes,
        Gen0Bytes         = s.Gen0Bytes,
        Gen1Bytes         = s.Gen1Bytes,
        Gen2Bytes         = s.Gen2Bytes,
        LohBytes          = s.LohBytes,
        LohLiveBytes      = s.LohLiveBytes,
        PohBytes          = s.PohBytes,
        FrozenBytes       = s.FrozenBytes,
        FragmentationPct  = s.FragmentationPct,
        HeapFreeBytes     = s.HeapFreeBytes,
        TotalObjectCount  = s.TotalObjectCount,
        LohObjectCount    = s.LohObjectCount,
        TopTypes          = s.TopTypes,
        ExceptionCounts   = s.ExceptionCounts.Select(x => new NameCount(x.Type,   x.Count)).ToList(),
        AsyncBacklogTotal = s.AsyncBacklogTotal,
        TopAsyncMethods   = s.TopAsyncMethods.Select(x =>  new NameCount(x.Method, x.Count)).ToList(),
        TimerCount        = s.TimerCount,
        WcfObjectCount    = s.WcfObjectCount,
        WcfFaultedCount   = s.WcfFaultedCount,
        ConnectionCount   = s.ConnectionCount,
        PinnedHandleCount = s.PinnedHandleCount,
        WeakHandleCount   = s.WeakHandleCount,
        StrongHandleCount = s.StrongHandleCount,
        TotalHandleCount  = s.TotalHandleCount,
        FinalizerQueueDepth = s.FinalizerQueueDepth,
        TopFinalizerTypes = s.TopFinalizerTypes.Select(x => new NameCount(x.Type, x.Count)).ToList(),
        ThreadCount          = s.ThreadCount,
        AliveThreadCount     = s.AliveThreadCount,
        BlockedThreadCount   = s.BlockedThreadCount,
        ExceptionThreadCount = s.ExceptionThreadCount,
        TpMinWorkers    = s.TpMinWorkers,
        TpMaxWorkers    = s.TpMaxWorkers,
        TpActiveWorkers = s.TpActiveWorkers,
        TpIdleWorkers   = s.TpIdleWorkers,
        EventLeakFieldCount  = s.EventLeakFieldCount,
        EventSubscriberTotal = s.EventSubscriberTotal,
        EventLeakMaxOnField  = s.EventLeakMaxOnField,
        TopEventLeaks        = s.TopEventLeaks,
        StringTotalBytes      = s.StringTotalBytes,
        StringWastedBytes     = s.StringWastedBytes,
        StringDuplicateGroups = s.StringDuplicateGroups,
        UniqueStringCount     = s.UniqueStringCount,
        TopStringDuplicates   = s.TopStringDuplicates,
        TopRootedTypes        = s.TopRootedTypes,
        ModuleCount           = s.ModuleCount,
        AppModuleCount        = s.AppModuleCount,
        Findings              = s.Findings,
        HealthScore           = s.HealthScore,
        SubReport             = subReport,
    };

    public DumpSnapshot ToSnapshot() => new()
    {
        DumpPath          = DumpPath,
        DumpFileSizeBytes = DumpFileSizeBytes,
        FileTime          = FileTime,
        ClrVersion        = ClrVersion,
        IsFullMode        = IsFullMode,
        TotalHeapBytes    = TotalHeapBytes,
        Gen0Bytes         = Gen0Bytes,
        Gen1Bytes         = Gen1Bytes,
        Gen2Bytes         = Gen2Bytes,
        LohBytes          = LohBytes,
        LohLiveBytes      = LohLiveBytes,
        PohBytes          = PohBytes,
        FrozenBytes       = FrozenBytes,
        FragmentationPct  = FragmentationPct,
        HeapFreeBytes     = HeapFreeBytes,
        TotalObjectCount  = TotalObjectCount,
        LohObjectCount    = LohObjectCount,
        TopTypes          = TopTypes,
        ExceptionCounts   = ExceptionCounts.Select(x => (x.Name, x.Count)).ToList(),
        AsyncBacklogTotal = AsyncBacklogTotal,
        TopAsyncMethods   = TopAsyncMethods.Select(x => (x.Name, x.Count)).ToList(),
        TimerCount        = TimerCount,
        WcfObjectCount    = WcfObjectCount,
        WcfFaultedCount   = WcfFaultedCount,
        ConnectionCount   = ConnectionCount,
        PinnedHandleCount = PinnedHandleCount,
        WeakHandleCount   = WeakHandleCount,
        StrongHandleCount = StrongHandleCount,
        TotalHandleCount  = TotalHandleCount,
        FinalizerQueueDepth = FinalizerQueueDepth,
        TopFinalizerTypes = TopFinalizerTypes.Select(x => (x.Name, x.Count)).ToList(),
        ThreadCount          = ThreadCount,
        AliveThreadCount     = AliveThreadCount,
        BlockedThreadCount   = BlockedThreadCount,
        ExceptionThreadCount = ExceptionThreadCount,
        TpMinWorkers    = TpMinWorkers,
        TpMaxWorkers    = TpMaxWorkers,
        TpActiveWorkers = TpActiveWorkers,
        TpIdleWorkers   = TpIdleWorkers,
        EventLeakFieldCount  = EventLeakFieldCount,
        EventSubscriberTotal = EventSubscriberTotal,
        EventLeakMaxOnField  = EventLeakMaxOnField,
        TopEventLeaks        = TopEventLeaks,
        StringTotalBytes      = StringTotalBytes,
        StringWastedBytes     = StringWastedBytes,
        StringDuplicateGroups = StringDuplicateGroups,
        UniqueStringCount     = UniqueStringCount,
        TopStringDuplicates   = TopStringDuplicates,
        TopRootedTypes        = TopRootedTypes,
        ModuleCount           = ModuleCount,
        AppModuleCount        = AppModuleCount,
        Findings              = Findings,
        HealthScore           = HealthScore,
    };
}

// ── Root export envelope ──────────────────────────────────────────────────────

public sealed class RawTrendExport
{
    /// <summary>Always "trend-raw" — distinguishes this from a plain "report" envelope.</summary>
    public string             Format     { get; set; } = "trend-raw";
    public string             ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string             Version    { get; set; } = "2";
    public List<SnapshotData> Snapshots  { get; set; } = [];
}

// ── AOT-safe JSON source generation ──────────────────────────────────────────

[JsonSerializable(typeof(RawTrendExport))]
[JsonSerializable(typeof(DumpReportEnvelope))]
[JsonSerializable(typeof(ReportElement))]         // ensures polymorphic derived types are included
[JsonSourceGenerationOptions(
    WriteIndented        = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling  = JsonCommentHandling.Skip,
    AllowTrailingCommas  = true)]
internal sealed partial class RawTrendContext : JsonSerializerContext { }
