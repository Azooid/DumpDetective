using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class HandleLeakTraceCommand : ICommand
{
    private readonly HandleLeakTraceAnalyzer _analyzer;
    private readonly HandleLeakTraceReport   _report;

    public HandleLeakTraceCommand(HandleLeakTraceAnalyzer analyzer, HandleLeakTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "handle-leak-trace";
    public string Description        => "GCHandle leak analysis — detects handle leaks by comparing created vs. destroyed handles per type.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective handle-leak-trace <trace-file> [options]

        Analyzes GCHandle/Created and GCHandle/Destroyed events to detect:
          • Net handle growth (created - destroyed > 100 = leak suspected)
          • Which handle kinds are accumulating
          • Growth trend over the trace

        Collect with:
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4' (GCHandleKeyword)

        Options:
          --top <N>            Max handle kinds to show (default: 20)
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

            Core.Models.CommandData.HandleLeakTraceData? data = null;
            CommandBase.RunStatus("Parsing GCHandle events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Handle Leak Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "handle-leak-trace");
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
            "handle-leak-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
