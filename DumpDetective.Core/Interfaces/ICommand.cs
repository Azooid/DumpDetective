using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Core.Interfaces;

/// <summary>Distinguishes memory-dump commands from trace-file commands.</summary>
public enum CommandKind
{
    /// <summary>Operates on a Windows memory dump (.dmp / .mdmp).</summary>
    Memory,
    /// <summary>Operates on an ETW trace file (.nettrace / .etl).</summary>
    Trace,
}

/// <summary>
/// Implemented by every analysis command. Registered once in
/// <c>DumpDetective.Cli.CommandRegistry</c> — the single source of truth for
/// both CLI dispatch and full-analyze inclusion.
/// </summary>
public interface ICommand
{
    /// <summary>CLI sub-command name, e.g. <c>"heap-stats"</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown in the help listing.</summary>
    string Description { get; }

    /// <summary>
    /// <see langword="true"/> if the command should be included when running
    /// <c>analyze --full</c>. Commands that require additional arguments
    /// (e.g. <c>--type</c>, <c>--address</c>) or are multi-dump tools set this
    /// to <see langword="false"/>.
    /// </summary>
    bool IncludeInFullAnalyze { get; }

    /// <summary>
    /// Help-panel category heading, e.g. <c>"Heap / Memory"</c>.
    /// Used by <c>HelpPrinter</c> to group commands dynamically.
    /// Default covers the majority of dump commands.
    /// </summary>
    string Category => "Heap / Memory";

    /// <summary>
    /// Whether the command operates on a memory dump or a trace file.
    /// Determines which section of the help panel the command appears in.
    /// Defaults to <see cref="CommandKind.Memory"/>.
    /// </summary>
    CommandKind Kind => CommandKind.Memory;

    /// <summary>
    /// CLI entry point. Parses <paramref name="args"/>, opens a
    /// <see cref="DumpContext"/>, calls <see cref="Render"/>, and returns an
    /// exit code (0 = success, 1 = error).
    /// </summary>
    int Run(string[] args);

    /// <summary>
    /// Executes the analysis against <paramref name="ctx"/> and writes output to
    /// <paramref name="sink"/>. Called by both <see cref="Run"/> (standalone) and
    /// <c>AnalyzeCommand.RenderEmbeddedReports</c> (parallel full-analyze).
    /// Default implementation calls <see cref="BuildReport"/> then replays via
    /// <c>ReportDocReplay</c>.
    /// </summary>
    void Render(DumpContext ctx, IRenderSink sink);

    /// <summary>
    /// Per-dump gate for full-analyze inclusion.  Called at analyze-time (after the dump
    /// is known) to allow commands that depend on a pre-built cache (e.g. <c>.idom.idx</c>)
    /// to skip silently when that cache does not yet exist.
    /// Default: returns <see cref="IncludeInFullAnalyze"/>.
    /// </summary>
    bool CanIncludeInFullAnalyze(string dumpPath) => IncludeInFullAnalyze;

    /// <summary>
    /// Builds a serialisable <see cref="ReportDoc"/> document tree from the dump data.
    /// This is the canonical output path — <see cref="Render"/> replays it through any sink.
    /// Default implementation captures <see cref="Render"/> via a <c>CaptureSink</c>
    /// (wired by <c>ReportingBootstrap.Register()</c>).
    /// </summary>
    ReportDoc BuildReport(DumpContext ctx) =>
        CommandBase.ReportDocBuilder?.Invoke(this, ctx)
        ?? throw new InvalidOperationException(
            "CommandBase.ReportDocBuilder is not initialised — call ReportingBootstrap.Register() at startup.");
}
