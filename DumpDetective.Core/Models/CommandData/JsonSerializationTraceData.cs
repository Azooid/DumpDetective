namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from JSON serialization cost analysis from a .nettrace / .etl trace.
///
/// Two complementary signals are used:
///   1. GCAllocationTick events — types with System.Text.Json / Newtonsoft.Json names
///      reveal allocation pressure from JSON operations (sampled every ~100 KB).
///   2. CPU sampling (SampledProfile / PerfInfo) — call stacks containing JSON frames
///      reveal how much CPU time is spent inside JSON serialization / deserialization.
/// </summary>
public sealed record JsonSerializationTraceData(
    string  TraceInfo,
    string? FilteredProcess,

    // ── Allocation signal ──────────────────────────────────────────────────
    int  TotalAllocTicks,
    long EstimatedJsonAllocBytes,
    long EstimatedTotalAllocBytes,

    // ── CPU signal ─────────────────────────────────────────────────────────
    int    TotalCpuSamples,
    int    JsonCpuSamples,
    double JsonCpuPct,

    // ── Breakdowns ─────────────────────────────────────────────────────────
    IReadOnlyList<JsonTypeAllocSummary> TopAllocTypes,
    IReadOnlyList<JsonCallerSummary>    TopCallers,
    IReadOnlyList<JsonLibrarySummary>   ByLibrary,
    bool HasData);

/// <summary>Aggregate allocation data for a specific JSON-related type.</summary>
public sealed record JsonTypeAllocSummary(
    string TypeName,
    string Library,
    int    Ticks,
    long   EstimatedBytes,
    double PctOfJsonAlloc);

/// <summary>
/// A user-code call site that drives JSON serialization work.
/// Combines allocation pressure and CPU samples attributed to this frame.
/// </summary>
public sealed record JsonCallerSummary(
    string CallerFrame,
    string Library,
    long   EstimatedAllocBytes,
    int    AllocTicks,
    int    CpuSamples);

/// <summary>Per-library breakdown of total JSON cost.</summary>
public sealed record JsonLibrarySummary(
    string Library,
    long   AllocBytes,
    int    AllocTicks,
    int    CpuSamples,
    double CpuPct);
