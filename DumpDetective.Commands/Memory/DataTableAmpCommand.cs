namespace DumpDetective.Commands.Memory;

public sealed class DataTableAmpCommand : ICommand
{
    private readonly DataTableAmpAnalyzer _analyzer;
    private readonly DataTableAmpReport   _report;

    public DataTableAmpCommand(DataTableAmpAnalyzer analyzer, DataTableAmpReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "datatable-amp";
    public string Description        => "Measure DataTable/DataSet memory amplification vs. typed collections.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Retention / Leak Signals";

    private const string Help = """
        Usage: DumpDetective datatable-amp <dump-file> [options]

        Counts DataTable, DataRow, DataColumn, DataView, and DataSet instances on the
        managed heap. Reads table name, row count, and column count via field inspection.
        Computes an amplification factor: managed overhead vs. estimated raw data size.

        Options:
          --top <n>          Max DataTable instances to show (default 200)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        int top = a.GetInt("top", 200);

        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, CommandBase.GetOverrideInt("top", 200));

    private void RenderWith(DumpContext ctx, IRenderSink sink, int top)
    {
        CommandBase.RenderHeader("DataTable Amplification", ctx, sink);
        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;
        var data = _analyzer.Analyze(ctx, top);
        _report.Render(data, sink);
    }
}
