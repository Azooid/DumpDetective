using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Sinks;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Shared parameters parsed once in <see cref="TraceDumpAnalyzeCommand.Run"/> and
/// forwarded to every sub-analyzer without repeating individual arguments.
/// </summary>
public readonly struct TraceRunParams(
    int top, string? processFilter,
    bool filterSystem, bool filterUnresolved, double slowMs)
{
    public readonly int     Top              = top;
    public readonly string? ProcessFilter    = processFilter;
    public readonly bool    FilterSystem     = filterSystem;
    public readonly bool    FilterUnresolved = filterUnresolved;
    public readonly double  SlowMs           = slowMs;
}

/// <summary>
/// Encapsulates a single trace sub-analyzer + its report renderer so that
/// <see cref="TraceDumpAnalyzeCommand"/> can drive all 29 of them via a
/// single loop instead of 29 copy-pasted blocks.
///
/// Lifecycle:
///  1. <see cref="Run"/>               — Phase 1 (trace analysis).
///  2. <see cref="OnDumpAvailable"/>   — Phase 2 hook; default is a no-op.
///  3. <see cref="OnCorrelationAvailable"/> — Phase 3 hook; default is a no-op.
/// </summary>
public interface ITraceSubAnalyzer
{
    string Key { get; }
    string SectionTitle { get; }

    /// <summary>
    /// Runs analysis, writes the captured report into <paramref name="captured"/>, and
    /// stores the typed result in <paramref name="results"/> under <see cref="Key"/>.
    /// </summary>
    /// <returns>The <c>TraceInfo</c> string for console display, or <see langword="null"/>.</returns>
    string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                Dictionary<string, ReportDoc> captured,
                Dictionary<string, object?> results);

    /// <summary>
    /// Called after the dump heap walk. Default: no-op.
    /// Override in <c>AllocTraceSubAnalyzer</c> to re-render with live heap sizes.
    /// </summary>
    void OnDumpAvailable(string traceFileName, DumpSnapshot snap,
                         Dictionary<string, ReportDoc> captured,
                         Dictionary<string, object?> results, int top) { }

    /// <summary>
    /// Whether this sub-analyzer performs work in the post-correlation phase.
    /// When <see langword="true"/>, <see cref="OnCorrelationAvailable"/> is called
    /// wrapped in a <c>RunAnalyzer</c> status line.
    /// Default: <see langword="false"/>.
    /// </summary>
    bool HasCorrelationPhase => false;

    /// <summary>
    /// Called after cross-source correlation. Default: no-op.
    /// Override in <c>RootCauseSubAnalyzer</c> to run the root-cause chain analysis.
    /// </summary>
    /// <returns>TraceInfo string for console display, or <see langword="null"/>.</returns>
    string? OnCorrelationAvailable(string traceFileName,
                                   IReadOnlyList<CorrelationFinding> findings,
                                   Dictionary<string, ReportDoc> captured,
                                   Dictionary<string, object?> results, int top) => null;
}
