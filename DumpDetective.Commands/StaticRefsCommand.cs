namespace DumpDetective.Commands;

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
    public string Description        => "Enumerate non-null static reference fields and estimate retained size.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective static-refs <dump-file> [options]

        Options:
          -f, --filter <t>     Only types/fields containing <t>
          -e, --exclude <t>    Exclude types containing <t> (repeatable)
          -a, --addresses      Show object addresses
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        var a        = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? filter   = a.Filter;
        bool    showAddr = a.ShowAddresses;
        // Multi-value --exclude: collect all occurrences from raw args
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--exclude" or "-e")
                excludes.Add(args[i + 1]);

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, filter, excludes, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, null, null, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        string? filter, HashSet<string>? excludes, bool showAddr)
    {
        CommandBase.RenderHeader("Static Reference Fields", ctx, sink);

        var data = _analyzer.Analyze(ctx, filter, excludes);
        _report.Render(data, sink, showAddr);
    }
}
