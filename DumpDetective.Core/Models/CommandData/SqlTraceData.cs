namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from SQL/EF command trace analysis.
/// Populated from Microsoft.Data.SqlClient.EventSource and EntityFrameworkCore EventSource events.
/// </summary>
public sealed record SqlTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalCommands,
    int    TotalErrors,
    double AvgCommandMs,
    double MaxCommandMs,
    double TotalCommandMs,
    double SlowThresholdMs,
    int    SlowCommandCount,
    IReadOnlyList<SqlCommandEntry>   SlowCommands,
    IReadOnlyList<SqlQuerySummary>   TopQueries,
    IReadOnlyList<SqlDbSummary>      TopDatabases,
    /// <summary>Command durations bucketed per second. Used for timeline sparkline.</summary>
    IReadOnlyList<double>?           DurationTimeline,
    bool HasData);

/// <summary>Individual slow SQL command execution.</summary>
public sealed record SqlCommandEntry(
    string CommandText,
    string Database,
    double DurationMs,
    double StartTimeMs,
    int    ThreadId,
    bool   IsError);

/// <summary>Detected ORM or data-access library generating this query pattern.</summary>
public enum OrmKind
{
    Unknown,
    EfCore,
    EfSix,
    Dapper,
    NHibernate,
    AdoNet,
    PetaPoco,
    RepoDb,
}

/// <summary>Aggregate statistics per unique query pattern.</summary>
public sealed record SqlQuerySummary(
    string CommandText,
    int    ExecutionCount,
    double TotalMs,
    double MaxMs,
    double AvgMs,
    int    ErrorCount,
    /// <summary>Detected ORM layer; <c>Unknown</c> if unrecognized.</summary>
    OrmKind DetectedOrm = OrmKind.Unknown);

/// <summary>Aggregate statistics per database name.</summary>
public sealed record SqlDbSummary(
    string Database,
    int    CommandCount,
    double TotalMs,
    double AvgMs);
