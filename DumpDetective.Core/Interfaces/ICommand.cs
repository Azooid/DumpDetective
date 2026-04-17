using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Core.Interfaces;

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
