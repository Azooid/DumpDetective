namespace DumpDetective.Models;

public enum FindingSeverity { Info, Warning, Critical }

public sealed record Finding(
    FindingSeverity Severity,
    string          Category,
    string          Headline,
    string?         Detail  = null,
    string?         Advice  = null);
