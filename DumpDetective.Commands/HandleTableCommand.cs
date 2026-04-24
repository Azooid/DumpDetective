namespace DumpDetective.Commands;

public sealed class HandleTableCommand : ICommand
{
    private readonly HandleTableAnalyzer _analyzer;
    private readonly HandleTableReport   _report;

    public HandleTableCommand(HandleTableAnalyzer analyzer, HandleTableReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "handle-table";
    public string Description        => "Analyze the GC handle table by kind and object type.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective handle-table <dump-file> [options]

        Options:
          -n, --top <N>       Top N object types per handle kind (default: 5)
          -f, --filter <k>    Only show handles whose kind contains <k>
          -o, --output <f>    Write report to file (.html / .md / .txt / .json)
          -h, --help          Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top    = a.GetInt("top", 5);
        string? filter = a.Filter;
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top, filter));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 5, null);


    private void RenderWith(DumpContext ctx, IRenderSink sink, int top, string? filter)
    {
        CommandBase.RenderHeader("GC Handle Table", ctx, sink);

        var data = _analyzer.Analyze(ctx, filter);
        _report.Render(data, sink, top);
    }
}
