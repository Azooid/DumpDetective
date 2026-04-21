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
    public string Description        => "Detect potential deadlocks via heuristic stack-frame analysis.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective deadlock-detection <dump-file> [options]

        Options:
          --min-threads <N>    Minimum group size to flag (default: 2)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Note:
          Analysis is heuristic (stack-frame based). Use WinDbg !dlk for
          guaranteed Monitor-level deadlock detection on live data.
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int minThreads = a.GetInt("min-threads", 2);
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, minThreads));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 2);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int minThreads)
    {
        CommandBase.RenderHeader("Deadlock Detection", ctx, sink);

        var data = _analyzer.Analyze(ctx, minThreads);
        _report.Render(data, sink);
    }
}
