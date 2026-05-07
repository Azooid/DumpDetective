namespace DumpDetective.Core.Models.CommandData;

public sealed record ExceptionsTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalThrown,
    int UniqueTypes,
    IReadOnlyList<ExceptionTypeSummary> TopTypes,
    IReadOnlyList<ExceptionEvent> RecentEvents,
    /// <summary>Exceptions-per-second bucketed timeline (sorted). Used for sparkline.</summary>
    IReadOnlyList<double>? RateTimeline = null);

public sealed record ExceptionTypeSummary(
    string ExceptionType,
    int Count,
    string FirstMessage,
    string TopFrame);

public sealed record ExceptionEvent(
    string ExceptionType,
    string Message,
    double TimeMs,
    string TopFrame,
    int ThreadId);
