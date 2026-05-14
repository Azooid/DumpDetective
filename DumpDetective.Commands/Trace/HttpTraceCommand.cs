using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class HttpTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly HttpTraceAnalyzer _analyzer;
    private readonly HttpTraceReport   _report;

    public HttpTraceCommand(HttpTraceAnalyzer analyzer, HttpTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "http-trace";
    public string Description        => "HTTP request analysis from a .nettrace or .etl trace (latency, error rate, slow requests, top endpoints).";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "HTTP Trace";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, p.ProcessFilter, p.SlowMs, progress);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    public bool SupportsConsumer => true;

    public ITraceEventConsumer? CreateConsumer(TraceRunParams p, string traceFileName)
        => _analyzer.CreateConsumer(p.ProcessFilter, p.SlowMs);

    public string? CompleteFromConsumer(ITraceEventConsumer consumer, string traceFileName,
        TraceRunParams p, Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var d = _analyzer.BuildResult(consumer, traceFileName, p.Top, p.ProcessFilter);
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective http-trace <trace-file> [options]

        Parses ASP.NET Core (Microsoft-AspNetCore-Hosting) or IIS/ASPX
        (Microsoft-Windows-ASPNET) request events to report:
          • Request count, latency percentiles (avg / P95 / P99 / max)
          • Error rate (4xx + 5xx) with status code breakdown
          • Slowest individual requests with path and timing
          • Top endpoints by request count with average latency

        Collecting an HTTP trace:
          dotnet-trace:  dotnet trace collect -p <pid> --providers 'Microsoft-AspNetCore-Hosting:0xFFFF:5'
          dotnet-trace:  dotnet trace collect -p <pid> --profile asp.net
          PerfView:      Enable 'ASP.NET' or 'AspNetCoreHosting' provider

        Options:
          --top <N>            Top N slow/path entries to show (default: 20)
          --process <name>     Filter to a specific process name
          --slow-ms <ms>       Slow request threshold in ms (default: 1000)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective http-trace app.nettrace
          DumpDetective http-trace perf.etl --process w3wp --slow-ms 2000
          DumpDetective http-trace app.nettrace --output http-report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var    a             = CliArgs.Parse(args);
        int    top           = a.GetInt("top", 20);
        double slowMs        = a.GetInt("slow-ms", 1000);
        string? tracePath    = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");

        if (!GcTraceCommand.ValidateTrace(ref tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            HttpTraceData? data = null;
            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            try
            {
                CommandBase.RunStatus(Name, update =>
                    data = _analyzer.Analyze(trace!, Path.GetFileName(tracePath!), top, processFilter, slowMs, s => update($"{Name}  {s}")));
            }
            finally
            {
                trace?.Dispose();
            }

            _report.Render(data!, sink, top);
            GcTraceCommand.PrintOutputPath(outputPaths);
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
            "http-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
