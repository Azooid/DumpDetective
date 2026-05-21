using DumpDetective.Core.Tracing;

namespace DumpDetective.Core.Interfaces;

/// <summary>
/// Optional plugin extension point for adding custom findings to
/// <c>trace-dump-analyze</c> cross-source correlation.
///
/// Implement this on a plugin command type and enable plugins with
/// <c>--with-plugins</c> to have the rule evaluated after built-in rules.
/// </summary>
public interface ITraceDumpCorrelationRule
{
    /// <summary>
    /// Stable identifier for diagnostics and future filtering support.
    /// Example: <c>"mycompany.sql-timeout-correlation"</c>.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Evaluates the rule and returns a finding when the condition matches,
    /// otherwise returns <see langword="null"/>.
    /// </summary>
    CorrelationFinding? Evaluate(TraceDumpCorrelationContext context);
}
