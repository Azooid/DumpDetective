namespace DumpDetective.Core.Models.CommandData;

public sealed record GcTraceData(
    string TraceInfo,
    string? FilteredProcess,
    int TotalGcs,
    double TotalPauseMs,
    double MaxPauseMs,
    double AvgPauseMs,
    IReadOnlyList<GcPauseEntry> TopPauses,
    IReadOnlyList<GcGenSummary> GenSummary,
    IReadOnlyList<GcEvent> Events);

public sealed record GcPauseEntry(
    int GcIndex,
    int Generation,
    string Reason,
    string Type,
    double PauseMs,
    long HeapSizeBefore,
    long HeapSizeAfter);

public sealed record GcGenSummary(
    int Generation,
    int Count,
    double TotalPauseMs,
    double MaxPauseMs,
    double AvgPauseMs);

public sealed record GcEvent(
    int GcIndex,
    int Generation,
    string Reason,
    string Type,
    double PauseMs,
    long HeapSizeBefore,
    long HeapSizeAfter,
    double TimeMs);
