namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>WeakRefsAnalyzer</c>.</summary>
public sealed record WeakRefsData(
    IReadOnlyList<WeakRefItem>     Handles,
    IReadOnlyList<CwtInstanceInfo> ConditionalWeakTables);

public sealed record WeakRefItem(string Kind, bool Alive, string Type, ulong Addr);

public sealed record CwtInstanceInfo(string TypeParam, int Entries);
