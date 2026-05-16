namespace DumpDetective.Commands.Memory;

/// <summary>
/// Produces a structured executive + engineering diagnostic summary by synthesizing
/// findings from multiple analyzers into a single opinionated narrative.
///
/// This command is designed to be the FIRST thing an engineer reads after capturing
/// a dump. It answers: "What is most likely wrong?" with scored evidence.
/// </summary>
public sealed class DiagnosticSummaryCommand : ICommand
{
    private readonly ConfigurationSmellAnalyzer  _configAnalyzer;
    private readonly WorkloadProfileClassifier   _workloadClassifier;
    private readonly ExceptionAnalysisAnalyzer   _exceptionAnalyzer;
    private readonly MemoryLeakAnalyzer          _leakAnalyzer;
    private readonly ThreadAnalysisAnalyzer      _threadAnalyzer;
    private readonly AsyncStacksAnalyzer         _asyncAnalyzer;
    private readonly DeadlockAnalyzer            _deadlockAnalyzer;
    private readonly DiagnosticSummaryReport     _report;

    public DiagnosticSummaryCommand(
        ConfigurationSmellAnalyzer  configAnalyzer,
        WorkloadProfileClassifier   workloadClassifier,
        ExceptionAnalysisAnalyzer   exceptionAnalyzer,
        MemoryLeakAnalyzer          leakAnalyzer,
        ThreadAnalysisAnalyzer      threadAnalyzer,
        AsyncStacksAnalyzer         asyncAnalyzer,
        DeadlockAnalyzer            deadlockAnalyzer,
        DiagnosticSummaryReport     report)
    {
        _configAnalyzer     = configAnalyzer;
        _workloadClassifier = workloadClassifier;
        _exceptionAnalyzer  = exceptionAnalyzer;
        _leakAnalyzer       = leakAnalyzer;
        _threadAnalyzer     = threadAnalyzer;
        _asyncAnalyzer      = asyncAnalyzer;
        _deadlockAnalyzer   = deadlockAnalyzer;
        _report             = report;
    }

    public string Name               => "diagnose";
    public string Description        => "Synthesize all analysis into an executive + engineering diagnostic summary.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective diagnose <dump-file> [options]

        Synthesizes findings from all major analyzers into a prioritized diagnostic
        summary with separate executive and engineering sections.

        Options:
          --workload <kind>  Override workload detection: aspnet | worker | data | console
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => Render(ctx, sink));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Diagnostic Summary", ctx, sink);

        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;

        // Collect all evidence in parallel where safe (read-only, different analyzers).
        var workload   = _workloadClassifier.Classify(ctx);
        var configData = _configAnalyzer.Analyze(ctx);
        var exceptions = _exceptionAnalyzer.Analyze(ctx);
        var asyncData  = _asyncAnalyzer.Analyze(ctx);
        var deadlocks  = _deadlockAnalyzer.Analyze(ctx);

        // Leak analysis uses BFS; run last and with lightweight mode (no root trace)
        // to keep the command fast by default.
        var leakData = _leakAnalyzer.Analyze(ctx, noRootTrace: true);

        // Thread analysis provides blocked counts / state breakdown.
        var threadData = _threadAnalyzer.Analyze(ctx);

        _report.Render(workload, configData, exceptions, leakData, asyncData, deadlocks, threadData, sink);
    }
}
