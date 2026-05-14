namespace DumpDetective.Core.Tracing;

/// <summary>
/// Aggregated score for a single diagnostic category derived from one or more
/// <see cref="TraceFinding"/> records.
///
/// Score range: 0–100.  Higher = more severe.
/// </summary>
public sealed record CategoryScore(
    string       Category,
    int          Score,
    int          FindingCount,
    TraceFinding TopFinding);
