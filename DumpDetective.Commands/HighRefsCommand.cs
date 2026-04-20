namespace DumpDetective.Commands;

public sealed class HighRefsCommand : ICommand
{
    private readonly HighRefsAnalyzer _analyzer;
    private readonly HighRefsReport   _report;

    public HighRefsCommand(HighRefsAnalyzer analyzer, HighRefsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "high-refs";
    public string Description        => "Detect objects with unusually high inbound reference counts.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective high-refs <dump-file> [options]

        Options:
          --top <n>           Number of objects to report (default 30)
          --min-refs <n>      Minimum inbound ref count (default 10)
          --addresses         Show object addresses in the table
          -o, --output <f>    Write report to file (.html / .md / .txt / .json)
          -h, --help          Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int  top      = a.GetInt("top",       30);
        int  minRefs  = a.GetInt("min-refs",  10);
        bool showAddr = a.ShowAddresses;

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top, minRefs, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, 50, 5, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int top, int minRefs, bool showAddr)
    {
        CommandBase.RenderHeader("Highly Referenced Object Analysis", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, top, minRefs);
        _report.Render(data, sink, minRefs, showAddr);
    }
}
