using DumpDetective.Core.Models;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// A semantic finding produced by an <see cref="ITracePatternDetector"/>.
/// Trace-specific: carries a score (0–100) and a confidence (0–1) that are
/// not present in the memory-domain <see cref="Finding"/> type.
/// <see cref="FindingSeverity"/> is reused so callers can compare severities
/// across both domains.
/// </summary>
public sealed record TraceFinding(
    FindingSeverity Severity,
    string          Category,
    string          Headline,
    string?         Detail,
    string?         Advice,
    int             Score,
    float           Confidence,
    string[]        EvidenceFrames);
