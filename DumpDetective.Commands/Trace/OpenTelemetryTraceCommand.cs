using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class OpenTelemetryTraceCommand : ICommand
{
    private readonly OpenTelemetryTraceAnalyzer _analyzer;
    private readonly OpenTelemetryTraceReport   _report;

    public OpenTelemetryTraceCommand(OpenTelemetryTraceAnalyzer analyzer, OpenTelemetryTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "otel-trace";
    public string Description        => "OpenTelemetry Activity analysis — measures span latency, error rates, and top operations from DiagnosticSource events.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective otel-trace <trace-file> [options]

        Analyzes System.Diagnostics.DiagnosticSource ActivityStart/Stop events to detect:
          • Slow activities (>1 second)
          • Error-tagged activities
          • Top operations by count and duration

        Collect with:
          dotnet-trace: --providers 'System.Diagnostics.DiagnosticSource:0xFF:5'

        Options:
          --top <N>            Max operations to show (default: 20)
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

            Core.Models.CommandData.OpenTelemetryTraceData? data = null;
            CommandBase.RunStatus("Parsing Activity/OTel events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("OpenTelemetry Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "otel-trace");
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
            "otel-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
