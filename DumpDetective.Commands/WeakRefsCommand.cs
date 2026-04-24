namespace DumpDetective.Commands;

public sealed class WeakRefsCommand : ICommand
{
    private readonly WeakRefsAnalyzer _analyzer;
    private readonly WeakRefsReport   _report;

    public WeakRefsCommand(WeakRefsAnalyzer analyzer, WeakRefsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "weak-refs";
    public string Description        => "Enumerates WeakShort/WeakLong GC handles and ConditionalWeakTable instances.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective weak-refs <dump-file> [options]

        Options:
          -a, --addresses    Show handle addresses
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a        = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, showAddr: false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, bool showAddr)
    {
        CommandBase.RenderHeader("Weak References", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
