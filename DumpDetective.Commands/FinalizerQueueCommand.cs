namespace DumpDetective.Commands;

public sealed class FinalizerQueueCommand : ICommand
{
    private readonly FinalizerQueueAnalyzer _analyzer;
    private readonly FinalizerQueueReport   _report;

    public FinalizerQueueCommand(FinalizerQueueAnalyzer analyzer, FinalizerQueueReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "finalizer-queue";
    public string Description        => "Inspect the GC finalizer queue for pending Finalize() calls.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective finalizer-queue <dump-file> [options]

        Options:
          -n, --top <N>      Top N types (default: 30)
          -a, --addresses    Show up to 20 object addresses per type
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int  top      = a.GetInt("top", 30);
        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 50, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int top, bool showAddr)
    {
        CommandBase.RenderHeader("Finalizer Queue", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, collectAddresses: showAddr);
        _report.Render(data, sink, top, showAddr);
    }
}
