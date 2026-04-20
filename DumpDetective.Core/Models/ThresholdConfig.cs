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
    // Health score thresholds for the trend signal table (lower score = worse)
    public double ScoreWarn          { get; set; } = 70;
    public double ScoreCrit          { get; set; } = 40;

    // Heap size (MB)
    public double HeapWarnMb         { get; set; } = 512;
    public double HeapCritMb         { get; set; } = 1024;

    // LOH (MB)
    public double LohWarnMb          { get; set; } = 100;
    public double LohCritMb          { get; set; } = 500;

    // Fragmentation free bytes (MB)
    public double FragWarnMb         { get; set; } = 50;
    public double FragCritMb         { get; set; } = 200;

    // Threads
    public double BlockedWarn        { get; set; } = 5;
    public double BlockedCrit        { get; set; } = 20;
    public double AsyncWarn          { get; set; } = 50;
    public double AsyncCrit          { get; set; } = 200;
    public double ExceptionWarn      { get; set; } = 1;
    public double ExceptionCrit      { get; set; } = 5;

    // Finalizer queue
    public double FinalizerWarn      { get; set; } = 100;
    public double FinalizerCrit      { get; set; } = 1000;

    // Timers
    public double TimerWarn          { get; set; } = 500;
    public double TimerCrit          { get; set; } = 2000;

    // WCF
    public double WcfWarn            { get; set; } = 1;
    public double WcfCrit            { get; set; } = 5;

    // DB connections
    public double DbWarn             { get; set; } = 50;
    public double DbCrit             { get; set; } = 200;

    // Pinned handles
    public double PinnedWarn         { get; set; } = 50;
    public double PinnedCrit         { get; set; } = 200;

    // Event subscribers
    public double EventWarn          { get; set; } = 1000;
    public double EventCrit          { get; set; } = 10000;

    // String waste (MB)
    public double StringWasteWarnMb  { get; set; } = 10;
    public double StringWasteCritMb  { get; set; } = 50;

    // Growth-based thresholds (used for secondary escalation)
    public double HeapGrowthWarnPct  { get; set; } = 20;
    public double HeapGrowthCritPct  { get; set; } = 50;
    public double FragGrowthWarnPct  { get; set; } = 5;
    public double Gen2GrowthWarnPct  { get; set; } = 10;
    public int    AsyncGrowthWarn    { get; set; } = 50;
    public int    BlockedGrowthWarn  { get; set; } = 3;
    public int    ScoreDropWarn      { get; set; } = 10;
    public int    ScoreDropCrit      { get; set; } = 25;
}
