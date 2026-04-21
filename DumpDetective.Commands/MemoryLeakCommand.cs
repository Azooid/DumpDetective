namespace DumpDetective.Commands;

public sealed class MemoryLeakCommand : ICommand
{
    private readonly MemoryLeakAnalyzer _analyzer;
    private readonly MemoryLeakReport   _report;

    public MemoryLeakCommand(MemoryLeakAnalyzer analyzer, MemoryLeakReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "memory-leak";
    public string Description        => "Four-step memory leak workflow: heap-stat, suspects, strings, GC roots.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective memory-leak <dump-file> [options]

        Options:
          -n, --top <N>         Top N types in heap-stat table (default: 30)
          --min-count <N>       Min instances for suspect table (default: 500)
          --no-root-trace       Skip GC root tracing (faster overview)
          --include-system      Include System.*/Microsoft.* in suspect table
          -o, --output <file>   Write report to file (.html / .md / .txt / .json)
          -h, --help            Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int  top          = a.GetInt("top",         30);
        int  minCount     = a.GetInt("min-count",   500);
        bool noRootTrace  = a.HasFlag("no-root-trace");
        bool inclSystem   = a.HasFlag("include-system");

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top, minCount, noRootTrace, inclSystem));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, 30, 500, false, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        int top, int minCount, bool noRootTrace, bool inclSystem)
    {
        CommandBase.RenderHeader("Memory Leak Analysis", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, top, minCount, noRootTrace, inclSystem);
        _report.Render(data, sink, top, inclSystem);
    }
}
