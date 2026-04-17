namespace DumpDetective.Core.Models;

/// <summary>
/// All heuristic thresholds for scoring and trend analysis.
/// Load from <c>dd-thresholds.json</c> to customise without recompiling.
/// All properties have built-in defaults — the file is optional.
/// </summary>
public sealed class ThresholdConfig
{
    public ScoringThresholds Scoring { get; set; } = new();
    public TrendThresholds   Trend   { get; set; } = new();
}

public sealed class ScoringThresholds
{
    // Heap size
    public long   HeapWarnMb        { get; set; } = 800;
    public long   HeapCritGb        { get; set; } = 2;
    public long   LohWarnMb         { get; set; } = 500;

    // Fragmentation
    public double FragWarnPct       { get; set; } = 20;
    public double FragCritPct       { get; set; } = 40;

    // Finalizer queue
    public int    FinalizerWarn     { get; set; } = 100;
    public int    FinalizerCrit     { get; set; } = 500;

    // Pinned handles
    public int    PinnedWarn        { get; set; } = 2000;

    // Event leaks
    public int    EventPerFieldCrit { get; set; } = 1000;
    public int    EventTotalWarn    { get; set; } = 500;

    // String duplication
    public long   StringWasteWarnMb { get; set; } = 100;

    // Async backlog
    public int    AsyncWarn         { get; set; } = 100;
    public int    AsyncCrit         { get; set; } = 500;

    // Thread pool
    public double TpNearCapacityPct { get; set; } = 0.8;

    // Threads
    public int    BlockedWarn       { get; set; } = 5;
    public int    BlockedCrit       { get; set; } = 20;
    public int    ExceptionWarn     { get; set; } = 5;

    // WCF
    public int    WcfFaultedWarn    { get; set; } = 1;

    // DB connections
    public int    DbConnectionWarn  { get; set; } = 10;
    public int    DbConnectionCrit  { get; set; } = 50;

    // Timers
    public int    TimerWarn         { get; set; } = 500;

    // Memory leak (Gen2 dominance)
    public double Gen2WarnPct       { get; set; } = 40;
    public double Gen2CritPct       { get; set; } = 60;
    public int    LeakTypeMinCount  { get; set; } = 1_000;

    // Score deductions
    public int    DeductHeapWarn      { get; set; } = 8;
    public int    DeductHeapCrit      { get; set; } = 15;
    public int    DeductLoh           { get; set; } = 10;
    public int    DeductFragWarn      { get; set; } = 5;
    public int    DeductFragCrit      { get; set; } = 10;
    public int    DeductFinalizerWarn { get; set; } = 5;
    public int    DeductFinalizerCrit { get; set; } = 15;
    public int    DeductPinned        { get; set; } = 5;
    public int    DeductEventCrit     { get; set; } = 20;
    public int    DeductEventWarn     { get; set; } = 10;
    public int    DeductString        { get; set; } = 5;
    public int    DeductAsyncWarn     { get; set; } = 5;
    public int    DeductAsyncCrit     { get; set; } = 10;
    public int    DeductTpWarn        { get; set; } = 5;
    public int    DeductTpCrit        { get; set; } = 15;
    public int    DeductBlockedWarn   { get; set; } = 5;
    public int    DeductBlockedCrit   { get; set; } = 10;
    public int    DeductException     { get; set; } = 5;
    public int    DeductWcf           { get; set; } = 10;
    public int    DeductDbWarn        { get; set; } = 5;
    public int    DeductDbCrit        { get; set; } = 10;
    public int    DeductTimer         { get; set; } = 5;
    public int    DeductLeakWarn      { get; set; } = 10;
    public int    DeductLeakCrit      { get; set; } = 20;
}

public sealed class TrendThresholds
{
    public double HeapGrowthWarnPct   { get; set; } = 20;
    public double HeapGrowthCritPct   { get; set; } = 50;
    public double FragGrowthWarnPct   { get; set; } = 5;
    public double Gen2GrowthWarnPct   { get; set; } = 10;
    public int    AsyncGrowthWarn     { get; set; } = 50;
    public int    BlockedGrowthWarn   { get; set; } = 3;
    public int    ScoreDropWarn       { get; set; } = 10;
    public int    ScoreDropCrit       { get; set; } = 25;
}
