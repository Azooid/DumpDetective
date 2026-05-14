namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from DB connection pool trace analysis.
/// Populated from SqlConnectionOpen / SqlConnectionClose events.
/// </summary>
public sealed record ConnectionPoolTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalOpens,
    int    TotalCloses,
    int    PeakOpenConnections,
    int    LeakedConnections,
    IReadOnlyList<ConnectionPoolDbSummary> TopDatabases,
    IReadOnlyList<double>?                 ConnectionTimeline,
    bool HasData);

/// <summary>Connection pool activity per database/connection string.</summary>
public sealed record ConnectionPoolDbSummary(
    string Database,
    int    Opens,
    int    Closes,
    int    NetOpen,
    double PeakConcurrent);
