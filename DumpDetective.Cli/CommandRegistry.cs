using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Commands;
using DumpDetective.Commands.Memory;
using DumpDetective.Commands.Trace;
using DumpDetective.Core.Interfaces;
using DumpDetective.Reporting.Reports;

namespace DumpDetective.Cli;

/// <summary>
/// Single source of truth for all registered commands.
/// Adding a new command: add one entry to <see cref="_commands"/> only.
/// </summary>
public static class CommandRegistry
{
    private static readonly ICommand[] _commands;

    static CommandRegistry()
    {
        // Phase 1: all individually-dispatchable commands (no orchestrators that need the list).
        // ── full-analyze: LPT order (longest first) ──────────────────────────
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

        ICommand[] analysisCommands =
        [
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

            // ── wave 2+: fill slots as wave-1 jobs finish ─────────────────────
            new ThreadPoolCommand(                          // ~18s — starts when thread-analysis frees at t≈17
                new ThreadPoolAnalyzer(),
                new ThreadPoolReport()),

            new DeadlockDetectionCommand(                   // ~1s with cache — starts when weak-refs frees at t≈18
                new DeadlockAnalyzer(),
                new DeadlockReport()),

            new HttpRequestsCommand(                        // ~12s — starts when deadlock frees at t≈19
                new HttpRequestsAnalyzer(),
                new HttpRequestsReport()),

            // ── fast (<1s typically, order is cosmetic) ───────────────────────
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

            new NativeInteropCommand(
                new NativeInteropAnalyzer(),
                new NativeInteropReport()),

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

            // ── not included in full-analyze ──────────────────────────────────
            new GcRootsCommand(
                new GcRootsAnalyzer(),
                new GcRootsReport()),

            ..TraceCommandRegistry.StandaloneCommands,

            // ── targeted / interactive ─────────────────────────────────────────
            new TypeInstancesCommand(
                new TypeInstancesAnalyzer(),
                new TypeInstancesReport()),

            new ObjectInspectCommand(),

            // ── diagnostic synthesis ────────────────────────────────────────────
            new DiagnosticSummaryCommand(
                new ConfigurationSmellAnalyzer(),
                new WorkloadProfileClassifier(),
                new ExceptionAnalysisAnalyzer(),
                new MemoryLeakAnalyzer(),
                new ThreadAnalysisAnalyzer(),
                new AsyncStacksAnalyzer(),
                new DeadlockAnalyzer(),
                new DiagnosticSummaryReport()),

            // ── cache lifecycle ─────────────────────────────────────────────
            new LoadCommand(),
            new CloseCommand(),
        ];

        // Phase 2: load plugins.  Built-in names always win — any plugin command
        // whose name clashes with a built-in (including orchestrators) is silently dropped.
        var reservedNames = new HashSet<string>(analysisCommands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var n in new[] { "analyze", "trend-analysis", "render", "diff",
                                   "trace-analyze", "trace-dump-analyze" })
            reservedNames.Add(n);

        var pluginCommands          = new List<ICommand>();
        var pluginTraceSubAnalyzers  = new List<ITraceSubAnalyzer>();
        var pluginTracePlugins       = new List<ITracePlugin>();
        _plugins = PluginLoader.LoadAll();
        foreach (var plugin in _plugins)
        {
            pluginTraceSubAnalyzers.AddRange(plugin.TraceSubAnalyzers);
            pluginTracePlugins.AddRange(plugin.TracePlugins);
            foreach (var cmd in plugin.Commands)
            {
                if (!reservedNames.Add(cmd.Name))
                {
                    Spectre.Console.AnsiConsole.MarkupLine(
                        $"[yellow]Plugin warning:[/] [dim]{plugin.Name}[/] — command [bold]{cmd.Name}[/] clashes with a built-in command, skipping.");
                    continue;
                }
                pluginCommands.Add(cmd);
            }
        }

        // Phase 3: build the full-analyze lists.
        // Built-in list is used by default; plugin commands are opt-in via --with-plugins.
        var builtInFullAnalyze = System.Array.FindAll(analysisCommands, static c => c.IncludeInFullAnalyze);
        IReadOnlyList<ICommand> pluginFullAnalyze = pluginCommands.Count > 0
            ? pluginCommands.Where(static c => c.IncludeInFullAnalyze).ToArray()
            : [];

        // Phase 4: assemble final array with orchestrators that depend on the lists.
        ICommand[] allDispatchable = pluginCommands.Count == 0
            ? analysisCommands
            : [..analysisCommands, ..pluginCommands];

        IReadOnlyList<ITraceSubAnalyzer> pluginTraceSubs = pluginTraceSubAnalyzers.Count > 0
            ? pluginTraceSubAnalyzers
            : [];
        IReadOnlyList<ITracePlugin> pluginTracePl = pluginTracePlugins.Count > 0
            ? pluginTracePlugins
            : [];

        _commands =
        [
            new AnalyzeCommand(builtInFullAnalyze, pluginFullAnalyze),
            ..allDispatchable,
            new TrendAnalysisCommand(builtInFullAnalyze, pluginFullAnalyze),
            ..TraceCommandRegistry.BuildOrchestratorCommands(pluginTraceSubs, pluginTracePl),
            new RenderCommand(),
            new DiffCommand(),
        ];
    }

    private static IReadOnlyList<LoadedPlugin> _plugins = [];

    /// <summary>All registered commands (built-in + plugin).</summary>
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

    /// <summary>Metadata for all successfully loaded plugins (empty when no plugins are present).</summary>
    internal static IReadOnlyList<LoadedPlugin> Plugins => _plugins;
}
