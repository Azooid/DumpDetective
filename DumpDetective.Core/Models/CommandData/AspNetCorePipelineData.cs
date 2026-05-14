namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from ASP.NET Core pipeline trace analysis.
/// Populated from Microsoft.AspNetCore.* EventSource events (routing, auth, diagnostics).
/// </summary>
public sealed record AspNetCorePipelineData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalRequests,
    int    TotalErrors,
    int    AuthFailures,
    int    UnmatchedRoutes,
    IReadOnlyList<AspNetCoreEndpointSummary> TopEndpoints,
    IReadOnlyList<double>?                   AuthFailureTimeline,
    bool HasData);

/// <summary>Per-endpoint request and error statistics.</summary>
public sealed record AspNetCoreEndpointSummary(
    string Route,
    int    Count,
    int    ErrorCount,
    int    AuthFailures,
    double TotalMs,
    double AvgMs);
