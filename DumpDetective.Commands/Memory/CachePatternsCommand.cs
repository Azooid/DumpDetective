namespace DumpDetective.Commands.Memory;

public sealed class CachePatternsCommand : ICommand
{
    private readonly CachePatternsAnalyzer _analyzer;
    private readonly CachePatternsReport   _report;

    public CachePatternsCommand(CachePatternsAnalyzer analyzer, CachePatternsReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "cache-patterns";
    public string Description        => "Detect unbounded caches and large collection instances.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Retention / Leak Signals";

    private const string Help = """
        Usage: DumpDetective cache-patterns <dump-file> [options]

        Scans the managed heap for Dictionary, ConcurrentDictionary, MemoryCache, HashSet,
        and similar collection types. Reads entry counts per instance and flags any
        collection instance exceeding the threshold as a potential unbounded cache.

        Options:
          --threshold <n>    Entry count threshold for 'unbounded' flag (default 10000)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        int threshold = a.GetInt("threshold", 10_000);

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, threshold));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, CommandBase.GetOverrideInt("threshold", 10_000));

    private void RenderWith(DumpContext ctx, IRenderSink sink, int threshold)
    {
        CommandBase.RenderHeader("Cache Pattern Detection", ctx, sink);
        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;
        var data = _analyzer.Analyze(ctx, threshold);
        _report.Render(data, sink, threshold);
    }
}
