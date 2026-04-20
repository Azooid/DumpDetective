namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ConnectionPoolAnalyzer</c>.</summary>
public sealed record ConnectionPoolData(
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<DbCommandEntry> Commands);

public sealed record ConnectionInfo(
    string TypeName,
    ulong  Addr,
    long   Size,
    string State,
    string ConnStr);

public sealed record DbCommandEntry(
    string TypeName,
    string CommandText);
