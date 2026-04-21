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

        // ── full-analyze: LPT order (longest first) ───────────────────────────
        // Parallel.For with NoBuffering picks the next item one-at-a-time in index
        // order. Placing the slowest jobs first fills all 8 workers with heavy work
        // from t=0 (LPT heuristic).
        //
        // IMPORTANT: deadlock-detection is NOT in the first 8 slots even though it
        // looks slow (~17s) — that time is entirely spent waiting on the ThreadNameMap
        // cache built by thread-analysis. Once the cache is ready deadlock takes ~1s.
        // Keeping it out of the first 8 lets memory-leak start at t=0.
        //
        // Empirical timings (large WCF dump, ~10 M objects):
        //   ~34s  static-refs
        //   ~33s  heap-fragmentation, large-objects
        //   ~25s  high-refs, memory-leak (when starting at t=0)
        //   ~21s  event-analysis
        //   ~18s  weak-refs, thread-pool
        //   ~17s  thread-analysis (triggers shared ThreadNameMap heap walk)
        //   ~12s  http-requests
        //    ~1s  deadlock-detection (reuses ThreadNameMap cache from thread-analysis)
        //   <1s   everything else

        // ── wave 1: first 8 slots, all start at t=0 ──────────────────────────
        new StaticRefsCommand(                          // ~34s
            new StaticRefsAnalyzer(),
            new StaticRefsReport()),

        new HeapFragmentationCommand(                   // ~33s
            new HeapFragmentationAnalyzer(),
            new HeapFragmentationReport()),

        new LargeObjectsCommand(                        // ~33s
            new LargeObjectsAnalyzer(),
            new LargeObjectsReport()),

        new MemoryLeakCommand(                          // ~25s — must be in first 8 (two heap walks)
            new MemoryLeakAnalyzer(),
            new MemoryLeakReport()),

        new HighRefsCommand(                            // ~25s
            new HighRefsAnalyzer(),
            new HighRefsReport()),

        new EventAnalysisCommand(                       // ~21s
            new EventAnalysisAnalyzer(),
            new EventAnalysisReport()),

        new ThreadAnalysisCommand(                      // ~17s — triggers shared ThreadNameMap heap walk
            new ThreadAnalysisAnalyzer(),
            new ThreadAnalysisReport()),

        new WeakRefsCommand(                            // ~18s
            new WeakRefsAnalyzer(),
            new WeakRefsReport()),

        // ── wave 2+: fill slots as wave-1 jobs finish ─────────────────────────
        new ThreadPoolCommand(                          // ~18s — starts when thread-analysis frees at t≈17
            new ThreadPoolAnalyzer(),
            new ThreadPoolReport()),

        new DeadlockDetectionCommand(                   // ~1s with cache — starts when weak-refs frees at t≈18
            new DeadlockAnalyzer(),
            new DeadlockReport()),

        new HttpRequestsCommand(                        // ~12s — starts when deadlock frees at t≈19
            new HttpRequestsAnalyzer(),
            new HttpRequestsReport()),

        // ── fast (<1s typically, order is cosmetic) ────────────────────────────
        new FinalizerQueueCommand(
            new FinalizerQueueAnalyzer(),
            new FinalizerQueueReport()),

        new StringDuplicatesCommand(
            new StringDuplicatesAnalyzer(),
            new StringDuplicatesReport()),

        new ConnectionPoolCommand(
            new ConnectionPoolAnalyzer(),
            new ConnectionPoolReport()),

        new WcfChannelsCommand(
            new WcfChannelsAnalyzer(),
            new WcfChannelsReport()),

        new ModuleListCommand(
            new ModuleListAnalyzer(),
            new ModuleListReport()),

        new HandleTableCommand(
            new HandleTableAnalyzer(),
            new HandleTableReport()),

        new HeapStatsCommand(
            new HeapStatsAnalyzer(),
            new HeapStatsReport()),

        new GenSummaryCommand(
            new GenSummaryAnalyzer(),
            new GenSummaryReport()),

        new AsyncStacksCommand(
            new AsyncStacksAnalyzer(),
            new AsyncStacksReport()),

        new PinnedObjectsCommand(
            new PinnedObjectsAnalyzer(),
            new PinnedObjectsReport()),

        new TimerLeaksCommand(
            new TimerLeaksAnalyzer(),
            new TimerLeaksReport()),

        new ExceptionAnalysisCommand(
            new ExceptionAnalysisAnalyzer(),
            new ExceptionAnalysisReport()),

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
