namespace DumpDetective.Core.Tracing;

/// <summary>
/// A rule that collapses one or more recognisable framework method-name patterns
/// into a single synthetic, human-readable frame label.
///
/// Matching is substring-based on the method's FullMethodName (case-insensitive).
/// The first rule whose <see cref="Patterns"/> array has ANY match wins.
/// </summary>
public sealed record CollapseRule(
    /// <summary>Substring patterns to match against FullMethodName. Any match triggers the rule.</summary>
    string[]    Patterns,
    /// <summary>The synthetic label that replaces the matched frames, e.g. "[ASP.NET Request Pipeline]".</summary>
    string      CollapsedLabel,
    /// <summary>Detector category this collapse belongs to — used for scoring.</summary>
    string      Category);
