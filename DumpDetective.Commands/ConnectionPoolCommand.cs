namespace DumpDetective.Commands;

public sealed class ConnectionPoolCommand : ICommand
{
    private readonly ConnectionPoolAnalyzer _analyzer;
    private readonly ConnectionPoolReport   _report;

    public ConnectionPoolCommand(ConnectionPoolAnalyzer analyzer, ConnectionPoolReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "connection-pool";
    public string Description        => "Analyze DB connection pool utilization and detect exhaustion.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective connection-pool <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses per connection (up to 200)
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
        CommandBase.RenderHeader("DB Connection Pool Analysis", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
