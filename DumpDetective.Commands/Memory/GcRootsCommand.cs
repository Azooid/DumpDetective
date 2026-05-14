namespace DumpDetective.Commands.Memory;

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
          -t, --type <name>       Type name to trace (case-insensitive substring)
          --address <0xADDR>      Trace a single object by address (skips heap scan; --type optional)
          -n, --max-results <N>   Max instances to trace when using --type (default: 10)
          --no-indirect           Skip 1-hop referrer scan (faster on large dumps)
          -o, --output <f>        Write report to file (.html / .md / .txt / .json)
          -h, --help              Show this help

        At least one of --type or --address is required.
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName   = a.GetOption("type") ?? a.GetOption("t");
        string? addrStr    = a.GetOption("address");
        int     maxResults = a.GetInt("max-results", 10);
        bool    noIndirect = a.HasFlag("no-indirect");

        ulong singleAddress = 0;
        if (addrStr is not null)
        {
            string normalized = addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? addrStr[2..] : addrStr;
            if (!ulong.TryParse(normalized, System.Globalization.NumberStyles.HexNumber,
                    null, out singleAddress))
            {
                AnsiConsole.MarkupLine($"[bold red]✗[/] Invalid address: {addrStr}");
                return 1;
            }
        }

        if (typeName is null && singleAddress == 0)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --type or --address is required.");
            return 1;
        }

        typeName ??= "<unknown>";

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, typeName, maxResults, noIndirect, singleAddress));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "gc-roots requires --type — use the Run() entry point.");


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        string typeName, int maxResults, bool noIndirect, ulong singleAddress = 0)
    {
        CommandBase.RenderHeader("GC Root Analysis", ctx, sink);

        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;

        var data = _analyzer.Analyze(ctx, typeName, maxResults, noIndirect, singleAddress);
        _report.Render(data, sink, noIndirect);
    }
}
