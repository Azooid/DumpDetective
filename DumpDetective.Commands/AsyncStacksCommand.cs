namespace DumpDetective.Commands;

public sealed class AsyncStacksCommand : ICommand
{
    private readonly AsyncStacksAnalyzer _analyzer;
    private readonly AsyncStacksReport   _report;

    public AsyncStacksCommand(AsyncStacksAnalyzer analyzer, AsyncStacksReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "async-stacks";
    public string Description        => "Enumerate async state machines and detect suspended task backlog.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective async-stacks <dump-file> [options]

        Options:
          -f, --filter <t>   Only show state machines whose type contains <t>
          -n, --top <N>      Top N methods (default: 50)
          -a, --addresses    Show individual state machine addresses (up to 200)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top      = a.GetInt("top", 50);
        bool    showAddr = a.ShowAddresses;

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 50, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int top, bool showAddr)
    {
        CommandBase.RenderHeader("Async State Machines", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, top, showAddr);
    }
}
