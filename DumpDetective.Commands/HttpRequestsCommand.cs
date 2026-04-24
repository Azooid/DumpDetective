namespace DumpDetective.Commands;

public sealed class HttpRequestsCommand : ICommand
{
    private readonly HttpRequestsAnalyzer _analyzer;
    private readonly HttpRequestsReport   _report;

    public HttpRequestsCommand(HttpRequestsAnalyzer analyzer, HttpRequestsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "http-requests";
    public string Description        => "Detect HttpClient leaks and in-flight HTTP request objects.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective http-requests <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses (up to 200)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, bool showAddr)
    {
        CommandBase.RenderHeader("HTTP Objects", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
