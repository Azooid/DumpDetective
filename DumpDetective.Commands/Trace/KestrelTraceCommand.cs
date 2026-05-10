using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class KestrelTraceCommand : ICommand
{
    private readonly KestrelTraceAnalyzer _analyzer;
    private readonly KestrelTraceReport   _report;

    public KestrelTraceCommand(KestrelTraceAnalyzer analyzer, KestrelTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "kestrel-trace";
    public string Description        => "Kestrel analysis — detects connection rejections, queue pressure, and request errors from Kestrel events.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective kestrel-trace <trace-file> [options]

        Analyzes Microsoft-AspNetCore-Server-Kestrel events to detect:
          • Connection rejections (overload)
          • Queue pressure (backlog buildup)
          • Request errors

        Collect with:
          dotnet-trace: --providers 'Microsoft-AspNetCore-Server-Kestrel:0xFF:5'

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

            Core.Models.CommandData.KestrelTraceData? data = null;
            CommandBase.RunStatus("Parsing Kestrel events...", _ =>
                data = _analyzer.Analyze(tracePath!, 20, processFilter));

            sink.Header("Kestrel Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "kestrel-trace");
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
            "kestrel-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
