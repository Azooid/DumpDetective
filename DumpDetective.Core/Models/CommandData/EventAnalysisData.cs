namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>EventAnalysisAnalyzer</c>.</summary>
public sealed record EventAnalysisData(IReadOnlyList<EventLeakGroup> Groups);

/// <summary>Aggregated subscriber count for one (publisher type, delegate field) pair.</summary>
public sealed record EventLeakGroup(string Publisher, string Field, int Subscribers);
