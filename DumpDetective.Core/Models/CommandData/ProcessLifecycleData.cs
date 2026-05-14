namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from process lifecycle trace analysis.
/// Populated from Windows Kernel/Process/Start and Process/Stop events.
/// </summary>
public sealed record ProcessLifecycleData(
    string TraceInfo,
    string? FilteredProcess,
    int    TotalStarts,
    int    TotalStops,
    int    DetectedRestarts,
    int    AbnormalStops,
    IReadOnlyList<ProcessEvent>       Events,
    IReadOnlyList<ProcessGroupSummary> ProcessGroups,
    bool HasData);

/// <summary>A single process start or stop event.</summary>
public sealed record ProcessEvent(
    string ProcessName,
    int    ProcessId,
    string EventType,
    double TimeMs,
    int?   ExitCode);

/// <summary>Aggregate lifecycle statistics for one process name.</summary>
public sealed record ProcessGroupSummary(
    string ProcessName,
    int    Starts,
    int    Stops,
    int    Restarts,
    int    AbnormalStops);
