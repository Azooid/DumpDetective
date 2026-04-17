namespace DumpDetective.Commands;

public sealed class TypeInstancesCommand : ICommand
{
    private readonly TypeInstancesAnalyzer _analyzer;
    private readonly TypeInstancesReport   _report;

    public TypeInstancesCommand(TypeInstancesAnalyzer analyzer, TypeInstancesReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "type-instances";
    public string Description        => "Find all instances of a type: counts, sizes, generation distribution.";
    public bool   IncludeInFullAnalyze => false;  // requires --type

    private const string Help = """
        Usage: DumpDetective type-instances <dump-file> --type <name> [options]

        Options:
          -t, --type <name>    Type to search (case-insensitive substring)  [required]
          -n, --top <N>        Max instances in detail view (default: 50)
          -a, --addresses      Show individual object addresses
          --min-size <bytes>   Only instances larger than N bytes
          --gen <0|1|2|loh>    Only instances in the specified generation
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName  = a.GetOption("type") ?? a.GetOption("t");
        int     top       = a.GetInt("top",       50);
        bool    showAddr  = a.ShowAddresses;
        long    minSize   = a.GetInt("min-size",    0);
        string? genFilter = a.GetOption("gen")?.ToLowerInvariant();

        if (typeName is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --type is required.");
            return 1;
        }

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, typeName, top, showAddr, minSize, genFilter));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "type-instances requires --type — use the Run() entry point.");


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        string typeName, int top, bool showAddr, long minSize, string? genFilter)
    {
        CommandBase.RenderHeader("Type Instances", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, typeName, top, minSize, genFilter);
        _report.Render(data, sink, showAddr);
    }
}
