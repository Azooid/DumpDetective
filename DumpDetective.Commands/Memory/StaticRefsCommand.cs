namespace DumpDetective.Commands.Memory;

public sealed class StaticRefsCommand : ICommand
{
    private readonly StaticRefsAnalyzer _analyzer;
    private readonly StaticRefsReport   _report;

    public StaticRefsCommand(StaticRefsAnalyzer analyzer, StaticRefsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "static-refs";
    public string Description        => "Enumerate non-null static reference fields and compute retained size.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective static-refs <dump-file> [options]

        Options:
          -f, --filter <t>       Only types/fields containing <t>
          -e, --exclude <t>      Exclude types containing <t> (repeatable)
          -a, --addresses        Show object addresses
              --bfs-depth <n>    BFS sample depth per declaring type.
                                 Enables sampling mode for faster, estimated retained sizes.
          -o, --output <f>       Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help
        """;

    public int Run(string[] args)
    {
        var a        = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? filter   = a.Filter;
        bool    showAddr = a.ShowAddresses;
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--exclude" or "-e")
                excludes.Add(args[i + 1]);

        // --bfs-depth N → N, default → null (exact mode)
        long? bfsDepth = a.GetOption("bfs-depth") is string d && long.TryParse(d, out long n) ? n
            : null;

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, filter, excludes, showAddr, bfsDepth));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        // Read overrides set by analyze --full / trend-analysis --full.
        long? bfsDepth = CommandBase.GetOverride("bfs-depth") is string bd && long.TryParse(bd, out long n) ? n
            : null;
        RenderWith(ctx, sink, null, null, false, bfsDepth);
    }

    // Called by AnalyzeCommand / TrendAnalysis via Render — accepts optional bfsDepth
    public void RenderWithDepth(DumpContext ctx, IRenderSink sink, long? bfsDepth)
        => RenderWith(ctx, sink, null, null, false, bfsDepth);

    private void RenderWith(DumpContext ctx, IRenderSink sink,
        string? filter, HashSet<string>? excludes, bool showAddr, long? bfsDepth)
    {
        CommandBase.RenderHeader("Static Reference Fields", ctx, sink);
        var data = _analyzer.Analyze(ctx, filter, excludes, bfsDepth);
        _report.Render(data, sink, showAddr);
    }
}
