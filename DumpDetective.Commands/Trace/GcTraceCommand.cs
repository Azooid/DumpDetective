using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class GcTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly GcTraceAnalyzer _analyzer;
    private readonly GcTraceReport   _report;

    public GcTraceCommand(GcTraceAnalyzer analyzer, GcTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "gc-trace";
    public string Description        => "GC pause analysis from a .nettrace or .etl trace (pause times, trigger reasons, heap sizes per collection).";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "GC Trace";

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
        Usage: DumpDetective gc-trace <trace-file> [options]

        Parses GC/Start and GC/Stop event pairs to report:
          • Per-generation GC counts and pause time statistics
          • Longest individual GC pauses with trigger reason and heap sizes
          • Alerts on blocking Gen 2 pauses > 100 ms and explicit GC.Collect() calls

        Collecting a GC trace:
          dotnet-trace:  dotnet trace collect --profile gc-verbose -p <pid>
          PerfView:      Enable 'GC' checkbox in collection dialog

        Options:
          --top <N>            Top N longest pauses to show (default: 30)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective gc-trace app.nettrace
                    DumpDetective gc-trace perf.etl --process w3wp --top 50
          DumpDetective gc-trace app.nettrace --output gc-report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 30);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");

        if (!ValidateTrace(tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            GcTraceData? data = null;
            CommandBase.RunStatus("Parsing GC events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            _report.Render(data!, sink, top);
            PrintOutputPath(outputPaths);
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
            "gc-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");

    internal static bool ValidateTrace(string? tracePath, string help)
    {
        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace or .etl).");
            AnsiConsole.MarkupLine(Markup.Escape(help));
            return false;
        }
        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return false;
        }
        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .nettrace or .etl — got: {Markup.Escape(Path.GetFileName(tracePath))}");
            return false;
        }
        return true;
    }

    internal static void PrintOutputPath(IReadOnlyList<string> outputPaths)
    {
        foreach (var p in outputPaths.Where(p => !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");
    }
}
