namespace DumpDetective.Commands;

public sealed class ThreadPoolCommand : ICommand
{
    private readonly ThreadPoolAnalyzer _analyzer;
    private readonly ThreadPoolReport   _report;

    public ThreadPoolCommand(ThreadPoolAnalyzer analyzer, ThreadPoolReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "thread-pool";
    public string Description        => "Reports thread pool worker saturation, Task state distribution, and queued work items.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective thread-pool <dump-file> [options]

        Options:
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        return CommandBase.Execute(a.DumpPath, a.OutputPath, Render);
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Thread Pool Analysis", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }

}
