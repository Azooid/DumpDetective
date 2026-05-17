namespace DumpDetective.Commands.Memory;

public sealed class GcRootMapCommand : ICommand
{
    private readonly GcRootMapAnalyzer _analyzer;
    private readonly GcRootMapReport   _report;

    public GcRootMapCommand(GcRootMapAnalyzer analyzer, GcRootMapReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "gc-root-map";
    public string Description        => "Classify all GC roots by kind and show top types held per root category.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "GC / Lifetime";

    private const string Help = """
        Usage: DumpDetective gc-root-map <dump-file> [options]

        Enumerates all GC handles (Strong, Pinned, WeakShort, WeakLong, AsyncPinned,
        RefCounted, SizedRef, Dependent) and thread stack roots. Groups by kind and
        shows which object types are most frequently held by each root category.

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
        CommandBase.RenderHeader("GC Root Classification", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }
}
