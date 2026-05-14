using DumpDetective.Core.Models;

namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from root cause chain analysis.
/// Derives ranked causal chains from all other trace analyzer outputs and
/// the correlation engine findings — no new ETW events required.
/// </summary>
public sealed record RootCauseChainData(
    string TraceInfo,
    int    TotalChains,
    FindingSeverity TopSeverity,
    IReadOnlyList<CausalChain> CausalChains,
    bool HasData);

/// <summary>A single causal chain from root cause through downstream effects.</summary>
public sealed record CausalChain(
    FindingSeverity Severity,
    int             Score,
    string          RootCause,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> Evidence,
    string          Advice,
    IReadOnlyList<string> ContributingAreas);
