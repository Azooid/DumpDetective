namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>TimerLeaksAnalyzer</c>.</summary>
public sealed record TimerLeaksData(IReadOnlyList<TimerItem> Timers);

public sealed record TimerItem(
    string Type,
    ulong  Addr,
    long   Size,
    string Callback,
    string Module,
    long   DueMs,
    long   PeriodMs);
