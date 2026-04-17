using DumpDetective.Analysis.Analyzers;
using DumpDetective.Commands;
using DumpDetective.Core.Interfaces;
using DumpDetective.Reporting.Reports;

namespace DumpDetective.Cli;

/// <summary>
/// Single source of truth for all registered commands.
/// Adding a new command: add one entry to <see cref="_commands"/> only.
/// </summary>
public static class CommandRegistry
{
    private static readonly ICommand[] _commands =
    [
        // ── orchestrator ──────────────────────────────────────────────────────
        new AnalyzeCommand(),

        // ── heap / memory ─────────────────────────────────────────────────────
        new HeapStatsCommand(
            new HeapStatsAnalyzer(),
            new HeapStatsReport()),

        new GenSummaryCommand(
            new GenSummaryAnalyzer(),
            new GenSummaryReport()),

        new HighRefsCommand(
            new HighRefsAnalyzer(),
            new HighRefsReport()),

        new StringDuplicatesCommand(
            new StringDuplicatesAnalyzer(),
            new StringDuplicatesReport()),

        new MemoryLeakCommand(
            new MemoryLeakAnalyzer(),
            new MemoryLeakReport()),

        new HeapFragmentationCommand(
            new HeapFragmentationAnalyzer(),
            new HeapFragmentationReport()),

        new LargeObjectsCommand(
            new LargeObjectsAnalyzer(),
            new LargeObjectsReport()),

        new PinnedObjectsCommand(
            new PinnedObjectsAnalyzer(),
            new PinnedObjectsReport()),

        new GcRootsCommand(
            new GcRootsAnalyzer(),
            new GcRootsReport()),

        new FinalizerQueueCommand(
            new FinalizerQueueAnalyzer(),
            new FinalizerQueueReport()),

        new HandleTableCommand(
            new HandleTableAnalyzer(),
            new HandleTableReport()),

        new StaticRefsCommand(
            new StaticRefsAnalyzer(),
            new StaticRefsReport()),

        new WeakRefsCommand(
            new WeakRefsAnalyzer(),
            new WeakRefsReport()),

        // ── threads / concurrency ─────────────────────────────────────────────
        new ThreadAnalysisCommand(
            new ThreadAnalysisAnalyzer(),
            new ThreadAnalysisReport()),

        new ThreadPoolCommand(
            new ThreadPoolAnalyzer(),
            new ThreadPoolReport()),

        new ThreadPoolStarvationCommand(
            new ThreadPoolStarvationAnalyzer(),
            new ThreadPoolStarvationReport()),

        new DeadlockDetectionCommand(
            new DeadlockAnalyzer(),
            new DeadlockReport()),

        new AsyncStacksCommand(
            new AsyncStacksAnalyzer(),
            new AsyncStacksReport()),

        // ── exceptions / diagnostics ──────────────────────────────────────────
        new ExceptionAnalysisCommand(
            new ExceptionAnalysisAnalyzer(),
            new ExceptionAnalysisReport()),

        new EventAnalysisCommand(
            new EventAnalysisAnalyzer(),
            new EventAnalysisReport()),

        // ── infrastructure / networking ───────────────────────────────────────
        new HttpRequestsCommand(
            new HttpRequestsAnalyzer(),
            new HttpRequestsReport()),

        new ConnectionPoolCommand(
            new ConnectionPoolAnalyzer(),
            new ConnectionPoolReport()),

        new WcfChannelsCommand(
            new WcfChannelsAnalyzer(),
            new WcfChannelsReport()),

        new TimerLeaksCommand(
            new TimerLeaksAnalyzer(),
            new TimerLeaksReport()),

        // ── targeted / interactive ────────────────────────────────────────────
        new TypeInstancesCommand(
            new TypeInstancesAnalyzer(),
            new TypeInstancesReport()),

        new ObjectInspectCommand(),

        new ModuleListCommand(
            new ModuleListAnalyzer(),
            new ModuleListReport()),

        // ── trend ─────────────────────────────────────────────────────────────
        new TrendAnalysisCommand(),
        new TrendRenderCommand(),
        new RenderCommand(),
    ];

    /// <summary>All registered commands.</summary>
    public static IEnumerable<ICommand> All => _commands;

    /// <summary>Commands included in a full-analyze run.</summary>
    public static IEnumerable<ICommand> FullAnalyzeCommands
        => System.Linq.Enumerable.Where(_commands, static c => c.IncludeInFullAnalyze);

    /// <summary>Finds a command by its CLI name, or returns <see langword="null"/>.</summary>
    public static ICommand? Find(string name)
    {
        foreach (var cmd in _commands)
            if (cmd.Name == name) return cmd;
        return null;
    }
}
