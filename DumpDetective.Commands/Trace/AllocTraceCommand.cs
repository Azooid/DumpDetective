using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class AllocTraceCommand : ICommand
{
    private readonly AllocTraceAnalyzer _analyzer;
    private readonly AllocTraceReport   _report;

    public AllocTraceCommand(AllocTraceAnalyzer analyzer, AllocTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "alloc-trace";
    public string Description        => "Allocation hotspot analysis from a .nettrace, .etl, or .etl.zip trace (GCAllocationTick — top allocating types and call sites).";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective alloc-trace <trace-file> [options]

        Parses GCAllocationTick events (sampled every ~100 KB) to report:
          • Top allocating types by estimated byte volume
          • Top allocating call sites (innermost user-code frame per tick)
          • Alert when a single type dominates allocations

        Note: GCAllocationTick is sampled (one event per ~100 KB). Totals are estimates.

        Collecting an allocation trace:
          dotnet-trace:  dotnet trace collect --profile gc-verbose -p <pid>
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>
          PerfView:      Enable 'GCAllocationTick' or use /GCOnly mode

        Options:
          --top <N>            Top N types / call sites to show (default: 20)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective alloc-trace app.nettrace
          DumpDetective alloc-trace perf.etl.zip --process w3wp --top 30
          DumpDetective alloc-trace app.nettrace --output alloc.html
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

            AllocTraceData? data = null;
            CommandBase.RunStatus("Parsing allocation events...", _ =>
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
            "alloc-trace requires a trace file (.nettrace, .etl, or .etl.zip) — it cannot analyze a memory dump.");
}
