using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Core.Interfaces;

/// <summary>
/// Minimal trace analysis interface for plugins.
///
/// Requires only <c>DumpDetective.Core</c> — no dependency on
/// <c>DumpDetective.Commands</c> or <c>DumpDetective.Reporting</c>.
/// The <c>TraceLog</c> type comes through Core's compile-time TraceEvent reference,
/// so plugin authors do not need a separate TraceEvent package reference.
///
/// Usage:
/// <code>
///   public sealed class MyTraceCommand : ICommand, ITracePlugin { ... }
/// </code>
///
/// The orchestrator (<c>trace-analyze --with-plugins</c> /
/// <c>trace-dump-analyze --with-plugins</c>) discovers this interface via
/// reflection and calls <see cref="Analyze"/> once per plugin, wrapping the output
/// in a capture sink automatically — the plugin writes to the provided
/// <see cref="IRenderSink"/> directly.
///
/// For advanced integration (consumer-based single-pass, correlation phase),
/// implement <c>ITraceSubAnalyzer</c> instead — that interface is in
/// <c>DumpDetective.Commands.Trace</c> and requires a project reference to Commands.
/// </summary>
public interface ITracePlugin
{
    /// <summary>Unique key used as a section ID in the combined report (e.g. <c>"trace-inventory"</c>).</summary>
    string Key { get; }

    /// <summary>Section heading rendered in the report (e.g. <c>"Trace Event Inventory"</c>).</summary>
    string SectionTitle { get; }

    /// <summary>
    /// Analyzes the trace and writes output to <paramref name="sink"/>.
    /// The orchestrator has already written a <c>Header</c> entry to the sink before
    /// calling this method.
    /// </summary>
    /// <param name="trace">The open, converted trace log. Do not dispose.</param>
    /// <param name="traceFileName">File name of the trace (for display).</param>
    /// <param name="top">Top-N items per section (from <c>--top</c>).</param>
    /// <param name="processFilter">Optional process name filter (from <c>--process</c>), or <see langword="null"/>.</param>
    /// <param name="sink">The sink to write output to. Already initialized with a section header.</param>
    /// <returns>Short summary string for console display (e.g. <c>"42 events from 8 providers"</c>), or <see langword="null"/>.</returns>
    string? Analyze(TraceLog trace, string traceFileName, int top, string? processFilter, IRenderSink sink);
}
