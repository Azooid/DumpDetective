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

public sealed class SocketTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly SocketTraceAnalyzer _analyzer;
    private readonly SocketTraceReport   _report;

    public SocketTraceCommand(SocketTraceAnalyzer analyzer, SocketTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "socket-trace";
    public string Description        => "Socket connect analysis — measures connection latency, failures, and top remote hosts from System.Net.Sockets events.";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "Socket Trace";

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
        Usage: DumpDetective socket-trace <trace-file> [options]

        Analyzes System.Net.Sockets EventSource events to detect:
          • Connection failures
          • Slow socket connects (>500 ms)
          • Top remote endpoints by connection volume

        Collect with:
          dotnet-trace: --providers 'System.Net.Sockets:0xFF:5'

        Options:
          --top <N>            Max hosts/operations to show (default: 20)
          --process <name>     Filter to process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
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

            Core.Models.CommandData.SocketTraceData? data = null;
            CommandBase.RunStatus("Parsing socket events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Socket Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "socket-trace");
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
            "socket-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
