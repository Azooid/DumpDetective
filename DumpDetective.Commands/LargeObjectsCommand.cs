namespace DumpDetective.Commands;

public sealed class LargeObjectsCommand : ICommand
{
    private readonly LargeObjectsAnalyzer _analyzer;
    private readonly LargeObjectsReport   _report;

    public LargeObjectsCommand(LargeObjectsAnalyzer analyzer, LargeObjectsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "large-objects";
    public string Description        => "Enumerate large objects, LOH segments, and fragmentation.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective large-objects <dump-file> [options]

        Options:
          -n, --top <N>          Top N objects by size (default: 50)
          -s, --min-size <bytes> Minimum object size (default: 85000)
          -f, --filter <name>    Only types whose name contains <name>
          -a, --addresses        Show object addresses
          --type-breakdown       Show aggregate by type only (no individual objects)
          -o, --output <file>    Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top           = a.GetInt("top",       50);
        long    minSize       = a.GetInt("min-size",   85_000);
        string? filter        = a.Filter;
        bool    showAddr      = a.ShowAddresses;
        bool    typeBreakdown = a.HasFlag("type-breakdown");

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top, minSize, filter, showAddr, typeBreakdown));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, 100, 85_000, null, false, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        int top, long minSize, string? filter, bool showAddr, bool typeBreakdown)
    {
        CommandBase.RenderHeader("Large Objects", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, minSize, filter);
        _report.Render(data, sink, top, showAddr, typeBreakdown);
    }
}
