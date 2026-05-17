using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Commands.Trace;
using DumpDetective.Core.Interfaces;
using DumpDetective.Reporting.Reports;

namespace DumpDetective.Cli;

/// <summary>
/// Single source of truth for all trace-analysis commands.
/// Adding a new trace command: add one entry to <see cref="_commands"/> only.
/// </summary>
public static class TraceCommandRegistry
{
    private static readonly ICommand[] _standaloneCommands =
    [
        // ── standalone single-analyzer trace commands ─────────────────────
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

        new ThreadPoolStarvationCommand(
            new ThreadPoolStarvationAnalyzer(),
            new ThreadPoolStarvationReport()),

        // ── Tier 1: GC / Memory / Concurrency ────────────────────────────
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

        // ── Tier 2: Network / IO / ASP.NET / Observability ───────────────
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

        // ── Intelligence: composite analyzers (depend on others) ──────────
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

    ];

    /// <summary>All 29 standalone commands as <see cref="ITraceSubAnalyzer"/> instances.</summary>
    public static IReadOnlyList<ITraceSubAnalyzer> SubAnalyzers { get; } = BuildSubAnalyzers();

    private static ITraceSubAnalyzer[] BuildSubAnalyzers()
    {
        var result = new ITraceSubAnalyzer[_standaloneCommands.Length];
        for (int i = 0; i < _standaloneCommands.Length; i++)
            result[i] = (ITraceSubAnalyzer)_standaloneCommands[i];
        return result;
    }

    /// <summary>
    /// The 29 standalone trace commands only (no orchestrators).
    /// Used by <see cref="CommandRegistry"/> which builds the orchestrators
    /// separately so it can inject plugin sub-analyzers.
    /// </summary>
    public static IReadOnlyList<ICommand> StandaloneCommands => _standaloneCommands;

    /// <summary>
    /// Creates <c>trace-analyze</c> and <c>trace-dump-analyze</c> orchestrators.
    /// Pass non-empty plugin lists to enable plugin participation when <c>--with-plugins</c> is set.
    /// </summary>
    public static ICommand[] BuildOrchestratorCommands(
        IReadOnlyList<ITraceSubAnalyzer>? pluginSubAnalyzers = null,
        IReadOnlyList<ITracePlugin>? pluginTracePlugins = null,
        IReadOnlyDictionary<string, string>? pluginTraceNames = null) =>
    [
        new TraceAnalyzeCommand(SubAnalyzers, pluginSubAnalyzers ?? [], pluginTracePlugins ?? [], pluginTraceNames),
        new TraceDumpAnalyzeCommand(
            SubAnalyzers,
            pluginSubAnalyzers ?? [],
            new TraceDumpCorrelationReport(),
            pluginTracePlugins ?? [],
            pluginTraceNames),
    ];

    private static readonly ICommand[] _commands =
    [
        .._standaloneCommands,
        new TraceAnalyzeCommand(SubAnalyzers, []),
        new TraceDumpAnalyzeCommand(
            SubAnalyzers,
            [],
            new TraceDumpCorrelationReport()),
    ];
    public static ICommand[] All => _commands;

    /// <summary>Finds a trace command by its CLI name, or returns <see langword="null"/>.</summary>
    public static ICommand? Find(string name)
    {
        foreach (var cmd in _commands)
            if (cmd.Name == name) return cmd;
        return null;
    }
}
