namespace DumpDetective.Commands;

public sealed class StringDuplicatesCommand : ICommand
{
    private readonly StringDuplicatesAnalyzer _analyzer;
    private readonly StringDuplicatesReport   _report;

    public StringDuplicatesCommand(StringDuplicatesAnalyzer analyzer, StringDuplicatesReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "string-duplicates";
    public string Description        => "Find duplicate string values wasting heap memory.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective string-duplicates <dump-file> [options]

        Options:
          --top <n>          Number of string groups to show (default 50)
          --min-count <n>    Minimum duplicate count (default 2)
          --min-waste <n>    Minimum wasted bytes (default 0)
          --pattern <str>    Filter by string content substring
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int     top      = a.GetInt("top",       50);
        int     minCount = a.GetInt("min-count",  2);
        long    minWaste = a.GetInt("min-waste",   0);
        string? pattern  = a.GetOption("pattern");

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, top, minCount, minWaste, pattern));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink,
            top:      CommandBase.GetOverrideInt("top",       100),
            minCount: CommandBase.GetOverrideInt("min-count",   2),
            minWaste: CommandBase.GetOverrideLong("min-waste",   0),
            pattern:  null);


    private void RenderWith(DumpContext ctx, IRenderSink sink,
        int top, int minCount, long minWaste, string? pattern)
    {
        CommandBase.RenderHeader("String Duplicates", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, top, minCount, minWaste, pattern);
    }
}
