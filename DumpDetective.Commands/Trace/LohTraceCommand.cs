using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class LohTraceCommand : ICommand
{
    private readonly LohTraceAnalyzer _analyzer;
    private readonly LohTraceReport   _report;

    public LohTraceCommand(LohTraceAnalyzer analyzer, LohTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "loh-trace";
    public string Description        => "LOH trend analysis — tracks Large Object Heap growth across GC collections to detect fragmentation.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective loh-trace <trace-file> [options]

        Analyzes GCHeapStats events to track LOH size over time and detect growth trends.

        Collect with:
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x1:4' (GCKeyword)

        Options:
          --process <name>     Filter to process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
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

            Core.Models.CommandData.LohTraceData? data = null;
            CommandBase.RunStatus("Analyzing LOH trend...", _ =>
                data = _analyzer.Analyze(tracePath!, 20, processFilter));

            sink.Header("LOH Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "loh-trace");
            _report.Render(data!, sink);
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
            "loh-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
