namespace DumpDetective.Commands;

public sealed class TimerLeaksCommand : ICommand
{
    private readonly TimerLeaksAnalyzer _analyzer;
    private readonly TimerLeaksReport   _report;

    public TimerLeaksCommand(TimerLeaksAnalyzer analyzer, TimerLeaksReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "timer-leaks";
    public string Description        => "Enumerates timer objects and alerts on accumulation.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective timer-leaks <dump-file> [options]

        Options:
          -a, --addresses    Show individual timer object addresses (up to 200 per type)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a        = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = a.ShowAddresses;
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, showAddr));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, showAddr: false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, bool showAddr)
    {
        CommandBase.RenderHeader("Timer Leak Analysis", ctx, sink);

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink, showAddr);
    }
}
