namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from DNS trace analysis.
/// Populated from System.Net.NameResolution EventSource events (.NET 5+).
/// </summary>
public sealed record DnsTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalResolutions,
    int    TotalFailed,
    double AvgResolutionMs,
    double MaxResolutionMs,
    IReadOnlyList<DnsHostSummary>      TopHostnames,
    IReadOnlyList<DnsSlowResolution>   SlowResolutions,
    IReadOnlyList<double>?             FailureTimeline,
    bool HasData);

/// <summary>Aggregate DNS resolution statistics per hostname.</summary>
public sealed record DnsHostSummary(
    string Hostname,
    int    Count,
    int    FailureCount,
    double TotalMs,
    double AvgMs,
    double MaxMs);

/// <summary>A single slow or failed DNS resolution.</summary>
public sealed record DnsSlowResolution(
    string Hostname,
    double DurationMs,
    bool   Failed,
    double TimeMs);
