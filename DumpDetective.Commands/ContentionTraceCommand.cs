using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

namespace DumpDetective.Commands;

public sealed class ContentionTraceCommand : ICommand
{
    private readonly ContentionTraceAnalyzer _analyzer;
    private readonly ContentionTraceReport   _report;

    public ContentionTraceCommand(ContentionTraceAnalyzer analyzer, ContentionTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "contention-trace";
    public string Description        => "Lock contention analysis from a .nettrace, .etl, or .etl.zip trace (hotspot call sites, wait times, threads affected).";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective contention-trace <trace-file> [options]

        Parses ContentionStart/Stop event pairs to report:
          • Top contention hotspots by total accumulated wait time
          • Worst individual lock acquisition delays
          • Threads affected and overall contention metrics

        Collecting a contention trace:
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x4000:4' -p <pid>
          PerfView:      Enable 'ContentionStacks' in additional providers

        Options:
          --top <N>            Top N hotspots / events to show (default: 20)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective contention-trace app.nettrace
          DumpDetective contention-trace perf.etl.zip --process w3wp
          DumpDetective contention-trace app.nettrace --output contention.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 20);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");

        if (!GcTraceCommand.ValidateTrace(tracePath, Help)) return 1;

        using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0 ? a.EffectiveOutputPaths : null);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            ContentionTraceData? data = null;
            CommandBase.RunStatus("Parsing contention events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            _report.Render(data!, sink, top);
            GcTraceCommand.PrintOutputPath(a);
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
            "contention-trace requires a trace file (.nettrace, .etl, or .etl.zip) — it cannot analyze a memory dump.");
}
