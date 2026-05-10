using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class FinalizerTraceCommand : ICommand
{
    private readonly FinalizerTraceAnalyzer _analyzer;
    private readonly FinalizerTraceReport   _report;

    public FinalizerTraceCommand(FinalizerTraceAnalyzer analyzer, FinalizerTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "finalizer-trace";
    public string Description        => "Finalizer queue analysis — detects finalization bursts, queue growth, and top finalizer types from GC events.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective finalizer-trace <trace-file> [options]

        Analyzes GCFinalizeObject and GCSuspendEE events to detect:
          • Finalization bursts (high finalization activity per GC)
          • Queue growth (queue not keeping up with finalization demand)
          • Top types by finalization count

        Collect with:
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x1:4' (GCKeyword)

        Options:
          --top <N>            Max finalizer types to show (default: 20)
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

            Core.Models.CommandData.FinalizerTraceData? data = null;
            CommandBase.RunStatus("Parsing finalizer events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Finalizer Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "finalizer-trace");
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
            "finalizer-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
