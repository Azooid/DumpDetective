namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// HTTP/ASP.NET request trace data from a .nettrace / .etl trace.
/// </summary>
public sealed record HttpTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalRequests,
    double TotalDurationMs,
    double AvgRequestMs,
    double MaxRequestMs,
    double P95RequestMs,
    double P99RequestMs,
    int ErrorCount,
    int SlowRequestCount,
    double SlowThresholdMs,
    IReadOnlyList<HttpRequestEntry> SlowRequests,
    IReadOnlyList<HttpStatusSummary> StatusSummary,
    IReadOnlyList<HttpPathSummary> TopPaths,
    /// <summary>Whether request events were found in the trace.</summary>
    bool HasData);

public sealed record HttpRequestEntry(
    string Method,
    string Path,
    int StatusCode,
    double DurationMs,
    double StartTimeMs,
    int ThreadId);

public sealed record HttpStatusSummary(
    int StatusCode,
    int Count,
    double AvgDurationMs);

public sealed record HttpPathSummary(
    string Path,
    int Count,
    double AvgDurationMs,
    double MaxDurationMs,
    int ErrorCount);
