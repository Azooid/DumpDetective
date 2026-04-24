namespace DumpDetective.Commands;

public sealed class DeadlockDetectionCommand : ICommand
{
    private readonly DeadlockAnalyzer _analyzer;
    private readonly DeadlockReport   _report;

    public DeadlockDetectionCommand(DeadlockAnalyzer analyzer, DeadlockReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "deadlock-detection";
    public string Description        => "Detect deadlocks via sync-block ownership and wait-chain analysis.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective deadlock-detection <dump-file> [options]

        Options:
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Analysis steps:
          1. Enumerate sync blocks to find inflated Monitor locks and their owners.
          2. Classify threads as Monitor-waiters, independent waiters, or active.
          3. Build a wait-for graph (waiter → owner) and detect cyclic dependencies.

        Severity:
          CRITICAL  — circular wait chain confirmed (T1 owns A, waits B; T2 owns B, waits A).
          WARNING   — contested lock(s) found but no cycle detected (contention, not deadlock).
          INFO      — independent waiting threads only (WaitOne/Task.Wait/timers — normal).
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths, Render);
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Deadlock Detection", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }
}
