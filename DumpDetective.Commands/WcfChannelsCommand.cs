namespace DumpDetective.Commands;

public sealed class WcfChannelsCommand : ICommand
{
    private readonly WcfChannelsAnalyzer _analyzer;
    private readonly WcfChannelsReport   _report;

    public WcfChannelsCommand(WcfChannelsAnalyzer analyzer, WcfChannelsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "wcf-channels";
    public string Description        => "Enumerate WCF channel objects and alert on faulted channels.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective wcf-channels <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, bool showAddr)
    {
        CommandBase.RenderHeader("WCF Channels", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
