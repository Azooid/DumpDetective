namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from socket trace analysis.
/// Populated from System.Net.Sockets EventSource events (.NET 5+).
/// </summary>
public sealed record SocketTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalConnects,
    int    TotalConnectFailed,
    double AvgConnectMs,
    double MaxConnectMs,
    int    SlowConnectCount,
    IReadOnlyList<SocketConnectEntry>       SlowConnects,
    IReadOnlyList<SocketHostSummary>        TopHosts,
    IReadOnlyList<double>?                  ConnectTimeline,
    bool HasData);

/// <summary>A slow or failed socket connect attempt.</summary>
public sealed record SocketConnectEntry(
    string RemoteEndpoint,
    double DurationMs,
    bool   Failed,
    double TimeMs);

/// <summary>Aggregate socket activity per remote host.</summary>
public sealed record SocketHostSummary(
    string Host,
    int    ConnectCount,
    int    FailureCount,
    double TotalMs,
    double AvgMs);
