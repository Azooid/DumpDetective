namespace DumpDetective.Commands;

public sealed class ModuleListCommand : ICommand
{
    private readonly ModuleListAnalyzer _analyzer;
    private readonly ModuleListReport   _report;

    public ModuleListCommand(ModuleListAnalyzer analyzer, ModuleListReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "module-list";
    public string Description        => "Lists all loaded managed assemblies categorized as App/System/GAC.";
    public bool   IncludeInFullAnalyze => true;

    private const string Help = """
        Usage: DumpDetective module-list <dump-file> [options]

        Options:
          -f, --filter <t>   Only show modules whose name contains <t>
          --app-only         Only show non-system assemblies
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public int Run(string[] args)
    {
        var a      = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;
        string? filter = a.Filter;
        bool appOnly   = a.HasFlag("app-only");
        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, filter, appOnly));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, null, false);


    private void RenderWith(DumpContext ctx, IRenderSink sink, string? filter, bool appOnly)
    {
        CommandBase.RenderHeader("Module List", ctx, sink);
        var data = _analyzer.Analyze(ctx, filter, appOnly);
        _report.Render(data, sink);
    }
}
