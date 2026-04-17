namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>WcfChannelsAnalyzer</c>.</summary>
public sealed record WcfChannelsData(IReadOnlyList<WcfObjectInfo> Objects);

public sealed record WcfObjectInfo(
    string Type,
    ulong  Addr,
    string State,
    string Endpoint,
    string Binding,
    string FaultReason);
