using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class AsyncTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly AsyncTraceAnalyzer _analyzer;
    private readonly AsyncTraceReport   _report;

    public AsyncTraceCommand(AsyncTraceAnalyzer analyzer, AsyncTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "async-trace";
    public string Description        => "Async/Task analysis — detects sync-over-async patterns, long-running continuations, and Task scheduling pressure.";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "Async / Task Trace";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, p.ProcessFilter);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective async-trace <trace-file> [options]

        Parses TPL Task lifecycle events to detect:
          • Synchronous blocking on async Tasks (.Wait()/.Result()) — sync-over-async
          • Long-running Task executions
          • Top continuation scheduling sites

        Collecting an async trace (TPL events require keyword 0x40):
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x40:5' -p <pid>
          PerfView:      PerfView.exe /ClrEvents:Tasks,Default /NoGui collect

        Options:
          --top <N>            Top N items per section (default: 20)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective async-trace app.nettrace
          DumpDetective async-trace perf.etl --process w3wp
          DumpDetective async-trace app.nettrace --output async.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 20);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");

        if (!GcTraceCommand.ValidateTrace(tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            Core.Models.CommandData.AsyncTraceData? data = null;
            CommandBase.RunStatus("Parsing async/Task events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Async Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "async-trace");
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
            "async-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
