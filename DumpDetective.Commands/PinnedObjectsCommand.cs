namespace DumpDetective.Commands;

public sealed class PinnedObjectsCommand : ICommand
{
    private readonly PinnedObjectsAnalyzer _analyzer;
    private readonly PinnedObjectsReport   _report;

    public PinnedObjectsCommand(PinnedObjectsAnalyzer analyzer, PinnedObjectsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "pinned-objects";
    public string Description        => "Shows GCHandle.Alloc(Pinned) and async-I/O pinned handles.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective pinned-objects <dump-file> [options]

        Options:
          -a, --addresses    Show individual object addresses (up to 100 per type)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a        = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, showAddr: false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, bool showAddr)
    {
        CommandBase.RenderHeader("Pinned Objects", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
