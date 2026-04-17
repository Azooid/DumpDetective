namespace DumpDetective.Commands;

public sealed class ThreadAnalysisCommand : ICommand
{
    private readonly ThreadAnalysisAnalyzer _analyzer;
    private readonly ThreadAnalysisReport   _report;

    public ThreadAnalysisCommand(ThreadAnalysisAnalyzer analyzer, ThreadAnalysisReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "thread-analysis";
    public string Description        => "Analyze managed threads: lifecycle, blocking state, exceptions.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective thread-analysis <dump-file> [options]

        Options:
          -s, --stacks          Show top-10 stack frames per thread
          -b, --blocked-only    Show only threads that appear blocked
          --state <s>           Filter: blocked | running | dead | all (default: all)
          --name <substr>       Filter by thread name (case-insensitive)
          -o, --output <file>   Write report to file (.html / .md / .txt / .json)
          -h, --help            Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool    showStacks   = a.HasFlag("stacks")       || a.HasFlag("s");
        bool    blockedOnly  = a.HasFlag("blocked-only") || a.HasFlag("b");
        string? nameFilter   = a.GetOption("name");
        string? stateFilter  = a.GetOption("state")?.ToLowerInvariant();

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, showStacks, blockedOnly, nameFilter, stateFilter));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, false, false, null, null);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        bool showStacks, bool blockedOnly, string? nameFilter, string? stateFilter)
    {
        CommandBase.RenderHeader("Thread Analysis", ctx, sink);

        var data = _analyzer.Analyze(ctx, captureStacks: showStacks);
        _report.Render(data, sink, showStacks, blockedOnly, nameFilter, stateFilter);
    }
}
