using DumpDetective.Analysis.Memory;
using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
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
    public string Description        => "Allocation hotspot analysis from a .nettrace or .etl trace (GCAllocationTick — top allocating types and call sites).";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective alloc-trace <trace-file> [options]

        Parses GCAllocationTick events (sampled every ~100 KB) to report:
          • Top allocating types by estimated byte volume
          • Top allocating call sites (innermost user-code frame per tick)
          • Alert when a single type dominates allocations

        Note: GCAllocationTick is sampled (one event per ~100 KB). Totals are estimates.

        Optionally, supply a dump file captured during the same incident with --dump
        to cross-reference estimated trace sizes against actual live heap sizes from
        the dump. This reveals:
          • Which types survived GC and are accumulating (live > estimated)
          • Which types were short-lived (allocated a lot but little remains)
          • Heap-dominant types that didn't show up in the trace (pre-existing survivors)

        Collecting an allocation trace:
          dotnet-trace:  dotnet trace collect --profile gc-verbose -p <pid>
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>
          PerfView:      Enable 'GCAllocationTick' or use /GCOnly mode

        Options:
          --top <N>            Top N types / call sites to show (default: 20)
          --process <name>     Filter to a specific process name
          --dump <file>        Memory dump (.dmp/.mdmp) for heap size cross-reference
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective alloc-trace app.nettrace
          DumpDetective alloc-trace perf.etl --process w3wp --top 30
          DumpDetective alloc-trace app.nettrace --dump app.dmp --output alloc.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 20);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault(CliArgs.IsTraceFile)
                                           ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");
        string? dumpPath      = a.GetOption("dump")
                                ?? a.Positionals.FirstOrDefault(static p =>
                                    p.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
                                    p.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase));

        if (!GcTraceCommand.ValidateTrace(tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            AllocTraceData? data = null;
            CommandBase.RunStatus("Parsing allocation events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            // ── Optional dump cross-reference ─────────────────────────────────
            IReadOnlyList<TypeStat>? dumpTopTypes  = null;
            long                     dumpHeapBytes = 0;
            if (dumpPath is not null)
            {
                if (!File.Exists(dumpPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Dump file not found, skipping cross-reference: {Markup.Escape(dumpPath)}");
                }
                else
                {
                    CommandBase.RunStatus("Reading dump heap type stats for size cross-reference...", _ =>
                    {
                        using var ctx  = DumpContext.Open(dumpPath);
                        var snap       = DumpCollector.CollectLightweight(ctx);
                        dumpTopTypes   = snap.TopTypes;
                        dumpHeapBytes  = snap.TotalHeapBytes;
                    });
                    AnsiConsole.MarkupLine($"  [green]✓[/] Dump cross-reference loaded  [dim]({dumpTopTypes?.Count ?? 0} types, heap {DumpHelpers.FormatSize(dumpHeapBytes)})[/]");
                }
            }

            _report.Render(data!, sink, top, dumpTopTypes, dumpHeapBytes);
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
            "alloc-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
