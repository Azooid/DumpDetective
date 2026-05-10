using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Analysis.Trace.Analyzers;
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

            new ThreadPoolStarvationCommand(
                new ThreadPoolStarvationAnalyzer(),
                new ThreadPoolStarvationReport()),

            new CpuTraceCommand(
                new CpuTraceAnalyzer(),
                new CpuTraceReport()),

            new GcTraceCommand(
                new GcTraceAnalyzer(),
                new GcTraceReport()),

            new ContentionTraceCommand(
                new ContentionTraceAnalyzer(),
                new ContentionTraceReport()),

            new ExceptionsTraceCommand(
                new ExceptionsTraceAnalyzer(),
                new ExceptionsTraceReport()),

            new AllocTraceCommand(
                new AllocTraceAnalyzer(),
                new AllocTraceReport()),

            new TraceAnalyzeCommand(
                new CpuTraceAnalyzer(),                 new CpuTraceReport(),
                new AllocTraceAnalyzer(),               new AllocTraceReport(),
                new GcTraceAnalyzer(),                  new GcTraceReport(),
                new ContentionTraceAnalyzer(),          new ContentionTraceReport(),
                new ExceptionsTraceAnalyzer(),          new ExceptionsTraceReport(),
                new ThreadPoolStarvationAnalyzer(),     new ThreadPoolStarvationReport(),
                new JitTraceAnalyzer(),                 new JitTraceReport(),
                new HttpTraceAnalyzer(),                new HttpTraceReport(),
                new AsyncTraceAnalyzer(),               new AsyncTraceReport(),
                new SqlTraceAnalyzer(),                 new SqlTraceReport(),
                new JsonSerializationTraceAnalyzer(),   new JsonSerializationTraceReport(),
                new ContextSwitchTraceAnalyzer(),       new ContextSwitchTraceReport(),
                new FinalizerTraceAnalyzer(),           new FinalizerTraceReport(),
                new ConnectionPoolTraceAnalyzer(),      new ConnectionPoolTraceReport(),
                new AllocationBurstAnalyzer(),          new AllocationBurstReport(),
                new DeadlockPatternAnalyzer(),          new DeadlockPatternReport(),
                new LohTraceAnalyzer(),                 new LohTraceReport(),
                new RetryStormAnalyzer(),               new RetryStormReport(),
                new ProcessLifecycleAnalyzer(),         new ProcessLifecycleReport(),
                new TaskSchedulerTraceAnalyzer(),       new TaskSchedulerTraceReport(),
                new FileIoTraceAnalyzer(),              new FileIoTraceReport(),
                new SocketTraceAnalyzer(),              new SocketTraceReport(),
                new DnsTraceAnalyzer(),                 new DnsTraceReport(),
                new KestrelTraceAnalyzer(),             new KestrelTraceReport(),
                new HandleLeakTraceAnalyzer(),          new HandleLeakTraceReport(),
                new AspNetCorePipelineAnalyzer(),       new AspNetCorePipelineReport(),
                new OpenTelemetryTraceAnalyzer(),       new OpenTelemetryTraceReport(),
                new AnomalyDetectionAnalyzer(),         new AnomalyDetectionReport(),
                new RootCauseChainAnalyzer(),           new RootCauseChainReport()),

            new JitTraceCommand(
                new JitTraceAnalyzer(),
                new JitTraceReport()),

            new HttpTraceCommand(
                new HttpTraceAnalyzer(),
                new HttpTraceReport()),

            new AsyncTraceCommand(
                new AsyncTraceAnalyzer(),
                new AsyncTraceReport()),

            new SqlTraceCommand(
                new SqlTraceAnalyzer(),
                new SqlTraceReport()),

            new JsonSerializationTraceCommand(
                new JsonSerializationTraceAnalyzer(),
                new JsonSerializationTraceReport()),

            new ContextSwitchTraceCommand(
                new ContextSwitchTraceAnalyzer(),
                new ContextSwitchTraceReport()),

            // ── Tier 1: New GC / Memory / Concurrency analyzers ──────────────
            new FinalizerTraceCommand(
                new FinalizerTraceAnalyzer(),
                new FinalizerTraceReport()),

            new AllocationBurstCommand(
                new AllocationBurstAnalyzer(),
                new AllocationBurstReport()),

            new DeadlockPatternCommand(
                new DeadlockPatternAnalyzer(),
                new DeadlockPatternReport()),

            new LohTraceCommand(
                new LohTraceAnalyzer(),
                new LohTraceReport()),

            new ConnectionPoolTraceCommand(
                new ConnectionPoolTraceAnalyzer(),
                new ConnectionPoolTraceReport()),

            new RetryStormCommand(
                new RetryStormAnalyzer(),
                new RetryStormReport()),

            new ProcessLifecycleCommand(
                new ProcessLifecycleAnalyzer(),
                new ProcessLifecycleReport()),

            new TaskSchedulerTraceCommand(
                new TaskSchedulerTraceAnalyzer(),
                new TaskSchedulerTraceReport()),

            // ── Tier 2: Network / IO / ASP.NET / Intelligence analyzers ──────
            new FileIoTraceCommand(
                new FileIoTraceAnalyzer(),
                new FileIoTraceReport()),

            new SocketTraceCommand(
                new SocketTraceAnalyzer(),
                new SocketTraceReport()),

            new DnsTraceCommand(
                new DnsTraceAnalyzer(),
                new DnsTraceReport()),

            new KestrelTraceCommand(
                new KestrelTraceAnalyzer(),
                new KestrelTraceReport()),

            new HandleLeakTraceCommand(
                new HandleLeakTraceAnalyzer(),
                new HandleLeakTraceReport()),

            new AspNetCorePipelineCommand(
                new AspNetCorePipelineAnalyzer(),
                new AspNetCorePipelineReport()),

            new OpenTelemetryTraceCommand(
                new OpenTelemetryTraceAnalyzer(),
                new OpenTelemetryTraceReport()),

            // ── Intelligence: composite analyzers (run last, depend on others) ──
            new AnomalyDetectionCommand(
                new CpuTraceAnalyzer(),
                new GcTraceAnalyzer(),
                new AllocationBurstAnalyzer(),
                new ContentionTraceAnalyzer(),
                new ExceptionsTraceAnalyzer(),
                new AnomalyDetectionAnalyzer(),
                new AnomalyDetectionReport()),

            new RootCauseTraceCommand(
                new CpuTraceAnalyzer(),
                new AllocTraceAnalyzer(),
                new GcTraceAnalyzer(),
                new ContentionTraceAnalyzer(),
                new ExceptionsTraceAnalyzer(),
                new ThreadPoolStarvationAnalyzer(),
                new JitTraceAnalyzer(),
                new HttpTraceAnalyzer(),
                new AsyncTraceAnalyzer(),
                new SqlTraceAnalyzer(),
                new FinalizerTraceAnalyzer(),
                new ConnectionPoolTraceAnalyzer(),
                new AllocationBurstAnalyzer(),
                new DeadlockPatternAnalyzer(),
                new LohTraceAnalyzer(),
                new RetryStormAnalyzer(),
                new RootCauseChainAnalyzer(),
                new RootCauseChainReport()),

            new TraceDumpAnalyzeCommand(
                new CpuTraceAnalyzer(),                 new CpuTraceReport(),
                new AllocTraceAnalyzer(),               new AllocTraceReport(),
                new GcTraceAnalyzer(),                  new GcTraceReport(),
                new ContentionTraceAnalyzer(),          new ContentionTraceReport(),
                new ExceptionsTraceAnalyzer(),          new ExceptionsTraceReport(),
                new ThreadPoolStarvationAnalyzer(),     new ThreadPoolStarvationReport(),
                new JitTraceAnalyzer(),                 new JitTraceReport(),
                new HttpTraceAnalyzer(),                new HttpTraceReport(),
                new AsyncTraceAnalyzer(),               new AsyncTraceReport(),
                new SqlTraceAnalyzer(),                 new SqlTraceReport(),
                new JsonSerializationTraceAnalyzer(),   new JsonSerializationTraceReport(),
                new ContextSwitchTraceAnalyzer(),       new ContextSwitchTraceReport(),
                new FinalizerTraceAnalyzer(),           new FinalizerTraceReport(),
                new ConnectionPoolTraceAnalyzer(),      new ConnectionPoolTraceReport(),
                new AllocationBurstAnalyzer(),          new AllocationBurstReport(),
                new DeadlockPatternAnalyzer(),          new DeadlockPatternReport(),
                new LohTraceAnalyzer(),                 new LohTraceReport(),
                new RetryStormAnalyzer(),               new RetryStormReport(),
                new ProcessLifecycleAnalyzer(),         new ProcessLifecycleReport(),
                new TaskSchedulerTraceAnalyzer(),       new TaskSchedulerTraceReport(),
                new FileIoTraceAnalyzer(),              new FileIoTraceReport(),
                new SocketTraceAnalyzer(),              new SocketTraceReport(),
                new DnsTraceAnalyzer(),                 new DnsTraceReport(),
                new KestrelTraceAnalyzer(),             new KestrelTraceReport(),
                new HandleLeakTraceAnalyzer(),          new HandleLeakTraceReport(),
                new AspNetCorePipelineAnalyzer(),       new AspNetCorePipelineReport(),
                new OpenTelemetryTraceAnalyzer(),       new OpenTelemetryTraceReport(),
                new AnomalyDetectionAnalyzer(),         new AnomalyDetectionReport(),
                new RootCauseChainAnalyzer(),           new RootCauseChainReport(),
                new TraceDumpCorrelationReport()),

            // ── targeted / interactive ─────────────────────────────────────────
            new TypeInstancesCommand(
                new TypeInstancesAnalyzer(),
                new TypeInstancesReport()),

            new ObjectInspectCommand(),

            // ── cache lifecycle ─────────────────────────────────────────────
            new LoadCommand(),
            new CloseCommand(),
        ];

        // Phase 2: derive the full-analyze subset, then assemble the final array
        // with the orchestrators that need it.
        var fullAnalyzeList = System.Array.FindAll(analysisCommands, static c => c.IncludeInFullAnalyze);

        _commands =
        [
            new AnalyzeCommand(fullAnalyzeList),
            ..analysisCommands,
            new TrendAnalysisCommand(fullAnalyzeList),
            new RenderCommand(),
            new DiffCommand(),
        ];
    }

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
