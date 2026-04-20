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

        // ── full-analyze order matches original canonical sequence ─────────────
        // heap / memory
        new HeapStatsCommand(
            new HeapStatsAnalyzer(),
            new HeapStatsReport()),

        new GenSummaryCommand(
            new GenSummaryAnalyzer(),
            new GenSummaryReport()),

        new HeapFragmentationCommand(
            new HeapFragmentationAnalyzer(),
            new HeapFragmentationReport()),

        new LargeObjectsCommand(
            new LargeObjectsAnalyzer(),
            new LargeObjectsReport()),

        new HighRefsCommand(
            new HighRefsAnalyzer(),
            new HighRefsReport()),

        // threads / concurrency
        new ExceptionAnalysisCommand(
            new ExceptionAnalysisAnalyzer(),
            new ExceptionAnalysisReport()),

        new ThreadAnalysisCommand(
            new ThreadAnalysisAnalyzer(),
            new ThreadAnalysisReport()),

        new ThreadPoolCommand(
            new ThreadPoolAnalyzer(),
            new ThreadPoolReport()),

        new AsyncStacksCommand(
            new AsyncStacksAnalyzer(),
            new AsyncStacksReport()),

        new DeadlockDetectionCommand(
            new DeadlockAnalyzer(),
            new DeadlockReport()),

        // gc / handles / leaks
        new FinalizerQueueCommand(
            new FinalizerQueueAnalyzer(),
            new FinalizerQueueReport()),

        new HandleTableCommand(
            new HandleTableAnalyzer(),
            new HandleTableReport()),

        new PinnedObjectsCommand(
            new PinnedObjectsAnalyzer(),
            new PinnedObjectsReport()),

        new WeakRefsCommand(
            new WeakRefsAnalyzer(),
            new WeakRefsReport()),

        new StaticRefsCommand(
            new StaticRefsAnalyzer(),
            new StaticRefsReport()),

        new TimerLeaksCommand(
            new TimerLeaksAnalyzer(),
            new TimerLeaksReport()),

        new EventAnalysisCommand(
            new EventAnalysisAnalyzer(),
            new EventAnalysisReport()),

        new StringDuplicatesCommand(
            new StringDuplicatesAnalyzer(),
            new StringDuplicatesReport()),

        // infrastructure / networking
        new WcfChannelsCommand(
            new WcfChannelsAnalyzer(),
            new WcfChannelsReport()),

        new ConnectionPoolCommand(
            new ConnectionPoolAnalyzer(),
            new ConnectionPoolReport()),

        new HttpRequestsCommand(
            new HttpRequestsAnalyzer(),
            new HttpRequestsReport()),

        new MemoryLeakCommand(
            new MemoryLeakAnalyzer(),
            new MemoryLeakReport()),

        new ModuleListCommand(
            new ModuleListAnalyzer(),
            new ModuleListReport()),

        // ── not included in full-analyze ──────────────────────────────────────
        new GcRootsCommand(
            new GcRootsAnalyzer(),
            new GcRootsReport()),

        new ThreadPoolStarvationCommand(
            new ThreadPoolStarvationAnalyzer(),
            new ThreadPoolStarvationReport()),

        // ── targeted / interactive ────────────────────────────────────────────
        new TypeInstancesCommand(
            new TypeInstancesAnalyzer(),
            new TypeInstancesReport()),

        new ObjectInspectCommand(),

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
