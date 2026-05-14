namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from retry storm trace analysis.
/// Correlates exception floods with HTTP error spikes to detect transient-fault retry patterns.
/// </summary>
public sealed record RetryStormData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalRetryExceptions,
    int    BurstCount,
    double PeakRetryRatePerMin,
    IReadOnlyList<RetryBurstEntry> RetryBursts,
    IReadOnlyList<string>          AffectedExceptionTypes,
    IReadOnlyList<double>?         RetryTimeline,
    bool HasData);

/// <summary>A window where retry-exception rate exceeded the burst threshold.</summary>
public sealed record RetryBurstEntry(
    double StartMs,
    double EndMs,
    int    ExceptionCount,
    double RatePerMin,
    string TopExceptionType);
