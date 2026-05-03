using DumpDetective.Core.Utilities;

namespace DumpDetective.Commands.Trace;

public sealed class ThreadPoolStarvationCommand : ICommand
{
    private readonly ThreadPoolStarvationAnalyzer _analyzer;
    private readonly ThreadPoolStarvationReport   _report;

    public ThreadPoolStarvationCommand(
        ThreadPoolStarvationAnalyzer analyzer,
        ThreadPoolStarvationReport   report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "threadpool-starvation";
    public string Description        => "Detect thread-pool starvation by parsing a .nettrace or .etl trace file.";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp

    private const string Help = """
        Usage: DumpDetective threadpool-starvation <trace-file> [options]

        Parses a .nettrace or .etl file for WaitHandleWait and ThreadPool
        adjustment events to surface potential starvation patterns.

        Supported input formats:
          .nettrace    EventPipe trace (dotnet-trace / VS diagnostic tools)
          .etl         Windows ETW trace (PerfView, xperf, WPR)

        Options:
          -n, --top <N>        Number of top wait events to display (default: 20)
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective threadpool-starvation perf.nettrace
          DumpDetective threadpool-starvation perf.etl --top 50 --output report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a        = CliArgs.Parse(args);
        int top      = a.GetInt("top", 20);
        string? tracePath = a.DumpPath ?? a.Positionals.FirstOrDefault();

        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace or .etl).");
            AnsiConsole.MarkupLine(Markup.Escape(Help));
            return 1;
        }

        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return 1;
        }

        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .nettrace or .etl — got: {Markup.Escape(Path.GetFileName(tracePath))}");
            return 1;
        }

        using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0 ? a.EffectiveOutputPaths : null);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath))}");

            var data = _analyzer.Analyze(tracePath, top);
            _report.Render(data, sink, top);

            foreach (var p in a.EffectiveOutputPaths.Where(p => !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");
            if (a.EffectiveOutputPaths.Count == 0 && sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(sink.FilePath)}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "threadpool-starvation requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");

}
