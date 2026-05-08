using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// A cross-analyzer correlation finding produced by <c>CorrelationEngine</c>.
/// Unlike <see cref="TraceFinding"/> (which is scoped to a single CPU call tree),
/// a <see cref="CorrelationFinding"/> is derived from the combined outputs of multiple
/// trace sub-analyzers and represents a causal relationship between two or more signals.
/// </summary>
public sealed record CorrelationFinding(
    /// <summary>Overall severity of the correlated pattern.</summary>
    FindingSeverity Severity,

    /// <summary>Short category label, e.g. "GC / Allocation" or "Threading".</summary>
    string Category,

    /// <summary>One-line description of the correlated pattern.</summary>
    string Headline,

    /// <summary>Detailed explanation referencing the contributing signals.</summary>
    string Detail,

    /// <summary>Actionable advice for addressing the root cause.</summary>
    string Advice,

    /// <summary>Confidence-weighted severity score, 0–100.</summary>
    int Score,

    /// <summary>
    /// Command names of the analyzers whose data contributed to this finding,
    /// e.g. ["gc-trace", "alloc-trace"]. Used for cross-linking in the report.
    /// </summary>
    string[] ContributingAreas);
