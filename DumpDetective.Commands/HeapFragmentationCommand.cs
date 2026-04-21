namespace DumpDetective.Commands;

public sealed class HeapFragmentationCommand : ICommand
{
    private readonly HeapFragmentationAnalyzer _analyzer;
    private readonly HeapFragmentationReport   _report;

    public HeapFragmentationCommand(HeapFragmentationAnalyzer analyzer, HeapFragmentationReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "heap-fragmentation";
    public string Description        => "Measures per-segment GC heap fragmentation and free-hole distribution.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective heap-fragmentation <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        return CommandBase.Execute(a.DumpPath, a.OutputPath, Render);
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Heap Fragmentation", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }

}
