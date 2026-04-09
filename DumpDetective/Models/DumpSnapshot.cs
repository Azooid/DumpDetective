namespace DumpDetective.Models;

public sealed class DumpSnapshot
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public required string   DumpPath        { get; init; }
    public          long     DumpFileSizeBytes { get; init; }
    public          DateTime FileTime        { get; init; }
    public          string?  ClrVersion      { get; set; }
    public          bool     IsFullMode      { get; init; }
    public          string   Label           => Path.GetFileNameWithoutExtension(DumpPath);

    // ── Memory by generation ─────────────────────────────────────────────────
    public long   TotalHeapBytes   { get; set; }
    public long   Gen0Bytes        { get; set; }
    public long   Gen1Bytes        { get; set; }
    public long   Gen2Bytes        { get; set; }
    public long   LohBytes         { get; set; }
    public long   PohBytes         { get; set; }
    public long   FrozenBytes      { get; set; }
    public double FragmentationPct { get; set; }

    // ── Object counts ─────────────────────────────────────────────────────────
    public long TotalObjectCount { get; set; }
    public long LohObjectCount   { get; set; }

    // ── Top types by size ─────────────────────────────────────────────────────
    public List<TypeStat> TopTypes { get; set; } = [];

    // ── Exceptions ────────────────────────────────────────────────────────────
    public List<(string Type, int Count)> ExceptionCounts { get; set; } = [];

    // ── Async state machines ──────────────────────────────────────────────────
    public int                               AsyncBacklogTotal { get; set; }
    public List<(string Method, int Count)>  TopAsyncMethods   { get; set; } = [];

    // ── Timers ────────────────────────────────────────────────────────────────
    public int TimerCount { get; set; }

    // ── WCF ───────────────────────────────────────────────────────────────────
    public int WcfObjectCount  { get; set; }
    public int WcfFaultedCount { get; set; }

    // ── DB connections ────────────────────────────────────────────────────────
    public int ConnectionCount { get; set; }

    // ── GC handles ────────────────────────────────────────────────────────────
    public int PinnedHandleCount { get; set; }
    public int WeakHandleCount   { get; set; }
    public int StrongHandleCount { get; set; }
    public int TotalHandleCount  { get; set; }

    // ── Finalizer queue ───────────────────────────────────────────────────────
    public int                               FinalizerQueueDepth { get; set; }
    public List<(string Type, int Count)>    TopFinalizerTypes   { get; set; } = [];

    // ── Threads ───────────────────────────────────────────────────────────────
    public int ThreadCount          { get; set; }
    public int AliveThreadCount     { get; set; }
    public int BlockedThreadCount   { get; set; }
    public int ExceptionThreadCount { get; set; }

    // ── Thread pool ───────────────────────────────────────────────────────────
    public int TpMinWorkers    { get; set; }
    public int TpMaxWorkers    { get; set; }
    public int TpActiveWorkers { get; set; }
    public int TpIdleWorkers   { get; set; }

    // ── Event leaks (full mode) ───────────────────────────────────────────────
    public int                   EventLeakFieldCount  { get; set; }
    public int                   EventSubscriberTotal { get; set; }
    public int                   EventLeakMaxOnField  { get; set; }
    public List<EventLeakStat>   TopEventLeaks        { get; set; } = [];

    // ── String duplicates (full mode) ─────────────────────────────────────────
    public long StringTotalBytes      { get; set; }
    public long StringWastedBytes     { get; set; }
    public int  StringDuplicateGroups { get; set; }
    public int  UniqueStringCount     { get; set; }
    public List<StringDuplicateStat> TopStringDuplicates { get; set; } = [];

    // ── Rooted objects by handle kind (all modes) ───────────────────────────
    public List<RootedHandleStat> TopRootedTypes { get; set; } = [];

    // ── Modules ───────────────────────────────────────────────────────────────
    public int ModuleCount    { get; set; }
    public int AppModuleCount { get; set; }

    // ── Scored findings ───────────────────────────────────────────────────────
    public List<Finding> Findings   { get; set; } = [];
    public int           HealthScore { get; set; } = 100;
}

public sealed record TypeStat(string Name, long Count, long TotalBytes);

public sealed record EventLeakStat(
    string PublisherType,
    string FieldName,
    int    Subscribers);

public sealed record StringDuplicateStat(string Value, int Count, long WastedBytes);

public sealed record RootedHandleStat(string HandleKind, string TypeName, int Count, long TotalBytes);
