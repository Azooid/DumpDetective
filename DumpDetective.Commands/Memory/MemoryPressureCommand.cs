namespace DumpDetective.Commands.Memory;

public sealed class MemoryPressureCommand : ICommand
{
    private readonly MemoryPressureAnalyzer _analyzer;
    private readonly MemoryPressureReport   _report;

    public MemoryPressureCommand(MemoryPressureAnalyzer analyzer, MemoryPressureReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "memory-pressure";
    public string Description        => "Correlate managed heap, GC generations, and thread stack memory usage.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Heap Overview";

    private const string Help = """
        Usage: DumpDetective memory-pressure <dump-file> [options]

        Provides a unified view of managed memory at the time of the dump:
          • Heap committed and reserved bytes by segment kind
          • Generation sizes (Gen0 / Gen1 / Gen2 / LOH / POH)
          • Thread stack memory (StackBase − StackLimit per alive thread)
          • Fragmentation percentage per segment kind
          • Alerts for high fragmentation or excessive thread stack consumption

        Options:
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => Render(ctx, sink));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Memory Pressure Correlation", ctx, sink);
        var data = _analyzer.Analyze(ctx);
        _report.Render(data, sink);
    }
}
