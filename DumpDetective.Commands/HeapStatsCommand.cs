namespace DumpDetective.Commands;

public sealed class HeapStatsCommand : ICommand
{
    private readonly HeapStatsAnalyzer _analyzer;
    private readonly HeapStatsReport   _report;

    public HeapStatsCommand(HeapStatsAnalyzer analyzer, HeapStatsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "heap-stats";
    public string Description        => "Show heap type statistics (size, count, generation).";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective heap-stats <dump-file> [options]

        Options:
          --top <n>          Number of types to show (default 50)
          --sort <col>       Sort by: size (default) | count | name
          --min-size <n>     Minimum total size in bytes
          --filter <str>     Filter by type name substring
          --gen <gen>        Filter by generation: gen0 | gen1 | gen2 | loh | poh
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top       = a.GetInt("top",      50);
        long    minSize   = a.GetInt("min-size",  0);
        string  sortBy    = a.GetString("sort",   "size");
        string? filter    = a.Filter;
        string? genFilter = a.GetOption("gen");

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top, minSize, sortBy, filter, genFilter));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, 100, 0, "size", null, null);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        int top, long minSize, string sortBy, string? filter, string? genFilter)
    {
        CommandBase.RenderHeader("Heap Statistics", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, filter, genFilter);
        _report.Render(data, sink, top, minSize, sortBy, genFilter);
    }
}
