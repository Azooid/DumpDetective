namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from OpenTelemetry / DiagnosticSource Activity trace analysis.
/// Populated from System.Diagnostics.DiagnosticSource ActivityStart/Stop events.
/// </summary>
public sealed record OpenTelemetryTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalActivities,
    int    TotalErrors,
    IReadOnlyList<OtelOperationSummary> TopOperations,
    IReadOnlyList<OtelSlowActivity>     SlowActivities,
    IReadOnlyList<double>?              ErrorRateTimeline,
    bool HasData);

/// <summary>Aggregate statistics per operation/span name.</summary>
public sealed record OtelOperationSummary(
    string OperationName,
    int    Count,
    int    ErrorCount,
    double TotalMs,
    double AvgMs,
    double MaxMs);

/// <summary>A single slow or error-annotated OpenTelemetry activity.</summary>
public sealed record OtelSlowActivity(
    string OperationName,
    double DurationMs,
    bool   IsError,
    double TimeMs);
