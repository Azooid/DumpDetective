namespace DumpDetective.Commands;

public sealed class GcRootsCommand : ICommand
{
    private readonly GcRootsAnalyzer _analyzer;
    private readonly GcRootsReport   _report;

    public GcRootsCommand(GcRootsAnalyzer analyzer, GcRootsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "gc-roots";
    public string Description        => "Trace GC roots keeping instances of a given type alive.";
    public bool   IncludeInFullAnalyze => false;  // too slow, requires --type

    private const string Help = """
        Usage: DumpDetective gc-roots <dump-file> --type <typename> [options]

        Options:
          -t, --type <name>       Type name to trace (case-insensitive substring)  [required]
          -n, --max-results <N>   Max instances to trace (default: 10)
          --no-indirect           Skip 1-hop referrer scan (faster on large dumps)
          -o, --output <f>        Write report to file (.html / .md / .txt / .json)
          -h, --help              Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName   = a.GetOption("type") ?? a.GetOption("t");
        int     maxResults = a.GetInt("max-results", 10);
        bool    noIndirect = a.HasFlag("no-indirect");

        if (typeName is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --type is required.");
            return 1;
        }

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, typeName, maxResults, noIndirect));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "gc-roots requires --type — use the Run() entry point.");


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        string typeName, int maxResults, bool noIndirect)
    {
        CommandBase.RenderHeader("GC Root Analysis", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx, typeName, maxResults, noIndirect);
        _report.Render(data, sink, noIndirect);
    }
}
