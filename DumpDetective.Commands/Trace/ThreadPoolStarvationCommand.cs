using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class ThreadPoolStarvationCommand : ICommand, ITraceSubAnalyzer
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
    public string Category             => "Threads & Concurrency";
    public CommandKind Kind               => CommandKind.Trace;
    public string Key                  => Name;
    public string SectionTitle         => "Thread Pool Starvation";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, progress);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    public bool SupportsConsumer => true;

    public ITraceEventConsumer? CreateConsumer(TraceRunParams p, string traceFileName)
        => _analyzer.CreateConsumer();

    public string? CompleteFromConsumer(ITraceEventConsumer consumer, string traceFileName,
        TraceRunParams p, Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var d = _analyzer.BuildResult(consumer, traceFileName, p.Top);
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

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

        // Resolve companion ETL → base so TraceLog.OpenOrConvert auto-merges all companions.
        string resolved = EtlPathHelper.ResolveToBase(tracePath);
        if (!string.Equals(resolved, tracePath, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[dim]↪ Companion ETL detected. Using base file: {Markup.Escape(Path.GetFileName(resolved))}[/]");
            tracePath = resolved;
        }
        var companions = EtlPathHelper.FindCompanionNames(tracePath!);
        if (companions.Count > 0)
            AnsiConsole.MarkupLine($"[dim]  + {companions.Count} companion file(s) will be auto-merged: {Markup.Escape(string.Join(", ", companions))}[/]");

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath))}");

            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            ThreadPoolStarvationData? data = null;
            CommandBase.RunStatus(Name, update =>
                data = _analyzer.Analyze(trace!, Path.GetFileName(tracePath!), top, s => update($"{Name}  {s}")));

            _report.Render(data!, sink, top);

            foreach (var p in outputPaths)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");
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
