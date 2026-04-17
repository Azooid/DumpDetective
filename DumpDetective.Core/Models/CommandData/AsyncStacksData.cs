namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>AsyncStacksAnalyzer</c>.</summary>
public sealed record AsyncStacksData(
    IReadOnlyList<StateMachineEntry> Entries,
    int                              BacklogTotal);

/// <summary>One heap-resident async state-machine instance.</summary>
public readonly record struct StateMachineEntry(string Method, string State, ulong Addr);
