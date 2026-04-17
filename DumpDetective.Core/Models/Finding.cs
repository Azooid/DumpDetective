namespace DumpDetective.Core.Models;

public enum FindingSeverity { Info, Warning, Critical }

/// <summary>
/// One health issue identified during dump analysis. Immutable record.
/// </summary>
public sealed record Finding(
    FindingSeverity Severity,
    string          Category,
    string          Headline,
    string?         Detail    = null,
    string?         Advice    = null,
    int             Deduction = 0);
