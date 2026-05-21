namespace DumpDetective.Commands.Memory;

public sealed class ClosureCaptureCommand : ICommand
{
    private readonly ClosureCaptureAnalyzer _analyzer;
    private readonly ClosureCaptureReport   _report;

    public ClosureCaptureCommand(ClosureCaptureAnalyzer analyzer, ClosureCaptureReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "closure-capture";
    public string Description        => "Find lambda/closure objects capturing large object graphs.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Retention / Leak Signals";

    private const string Help = """
        Usage: DumpDetective closure-capture <dump-file> [options]

        Walks the managed heap for compiler-generated closure display-class objects
        (<>c__DisplayClass*). Groups by declaring type, computes retained size via BFS,
        and shows captured field types for the top groups.

        Options:
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => Render(ctx, sink));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Closure Capture Analysis", ctx, sink);
        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }
}
