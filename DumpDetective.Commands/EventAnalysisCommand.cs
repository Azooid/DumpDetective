namespace DumpDetective.Commands;

public sealed class EventAnalysisCommand : ICommand
{
    private readonly EventAnalysisAnalyzer _analyzer;
    private readonly EventAnalysisReport   _report;

    public EventAnalysisCommand(EventAnalysisAnalyzer analyzer, EventAnalysisReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "event-analysis";
    public string Description        => "Detect event handler subscription leaks in the heap.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective event-analysis <dump-file> [options]

        Options:
          -n, --top <N>          Top N event fields (default: 20)
          -o, --output <f>       Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = a.GetInt("top", 20);
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 20);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int top)
    {
        CommandBase.RenderHeader("Event Analysis Report", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, top);
    }
}
