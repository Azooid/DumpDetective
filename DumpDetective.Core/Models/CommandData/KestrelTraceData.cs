namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from Kestrel HTTP server trace analysis.
/// Populated from Microsoft-AspNetCore-Server-Kestrel EventSource events.
/// </summary>
public sealed record KestrelTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalConnections,
    int    RejectedConnections,
    int    PeakConcurrentConnections,
    int    RequestErrors,
    bool   QueuePressureDetected,
    IReadOnlyList<double>? ConnectionTimeline,
    bool HasData);
