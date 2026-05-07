using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class ExceptionsTraceCommand : ICommand
{
    private readonly ExceptionsTraceAnalyzer _analyzer;
    private readonly ExceptionsTraceReport   _report;

    public ExceptionsTraceCommand(ExceptionsTraceAnalyzer analyzer, ExceptionsTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "exceptions-trace";
    public string Description        => "First-chance exception analysis from a .nettrace or .etl trace (exception flood detection, top types, call sites).";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective exceptions-trace <trace-file> [options]

        Parses Exception events to report:
          • Total and unique exception types thrown during the trace
          • Top exception types by count with originating call sites
          • Exception flood detection (> 1,000 or > 10,000 thrown)

        Collecting an exceptions trace:
          dotnet-trace:  dotnet trace collect --profile exceptions -p <pid>
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x8014:5' -p <pid>
          PerfView:      Enable 'ExceptionSampled' or 'Exception' in providers

        Options:
          --top <N>            Top N exception types / recent events to show (default: 20)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective exceptions-trace app.nettrace
                    DumpDetective exceptions-trace perf.etl --process w3wp --top 40
          DumpDetective exceptions-trace app.nettrace --output exceptions.html
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

            ExceptionsTraceData? data = null;
            CommandBase.RunStatus("Parsing exception events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

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
            "exceptions-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
