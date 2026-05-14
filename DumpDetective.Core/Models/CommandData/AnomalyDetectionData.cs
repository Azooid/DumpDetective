using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from anomaly detection analysis.
/// Uses rolling z-score detection over per-second metric timelines already
/// computed by the Tier 1 trace analyzers — no new ETW events required.
/// </summary>
public sealed record AnomalyDetectionData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalAnomalies,
    int    BaselineWindowSec,
    IReadOnlyList<TraceAnomaly> Anomalies,
    bool HasData);

/// <summary>A detected anomaly in a specific metric time series.</summary>
public sealed record TraceAnomaly(
    string MetricName,
    double TimeMs,
    double ObservedValue,
    double BaselineValue,
    double ZScore,
    FindingSeverity Severity,
    string Description);
