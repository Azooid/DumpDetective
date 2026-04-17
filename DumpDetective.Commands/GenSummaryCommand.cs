namespace DumpDetective.Commands;

public sealed class GenSummaryCommand : ICommand
{
    private readonly GenSummaryAnalyzer _analyzer;
    private readonly GenSummaryReport   _report;

    public GenSummaryCommand(GenSummaryAnalyzer analyzer, GenSummaryReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "gen-summary";
    public string Description        => "GC generation size and object count breakdown.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective gen-summary <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        return CommandBase.Execute(a.DumpPath, a.OutputPath, Render);
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("GC Generation Summary", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }

}
