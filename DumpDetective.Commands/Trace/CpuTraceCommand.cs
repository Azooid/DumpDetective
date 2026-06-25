using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class CpuTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly CpuTraceAnalyzer _analyzer;
    private readonly CpuTraceReport   _report;

    public CpuTraceCommand(CpuTraceAnalyzer analyzer, CpuTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "cpu-trace";
    public string Description        => "CPU hot-path analysis from a .nettrace or .etl trace file (call tree + hot path, VS-style).";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp
    public string Category             => "CPU & Allocation";
    public CommandKind Kind               => CommandKind.Trace;
    public string Key                  => Name;
    public string SectionTitle         => "CPU Trace";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, p.ProcessFilter, p.FilterSystem, p.FilterUnresolved, progress);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    public bool SupportsConsumer => true;

    public ITraceEventConsumer? CreateConsumer(TraceRunParams p, string traceFileName)
        => _analyzer.CreateConsumer(p.ProcessFilter, p.FilterSystem, p.FilterUnresolved);

    public string? CompleteFromConsumer(ITraceEventConsumer consumer, string traceFileName,
        TraceRunParams p, Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var d = _analyzer.BuildResult(consumer, traceFileName, p.Top, p.ProcessFilter, p.FilterSystem, p.FilterUnresolved);
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective cpu-trace <trace-file> [options]

        Parses CPU sampling events and produces:
          • Hot path  — the deepest chain of maximum CPU consumption (Visual Studio style)
          • Top methods by exclusive CPU time (the methods actually executing)
          • Call tree — inclusive/exclusive breakdown per caller → callee chain

        Supported input formats:
          .nettrace    EventPipe trace collected with --profile cpu-sampling
          .etl         Windows ETW trace with kernel CPU sampling

        Collecting a CPU trace:
          dotnet-trace:  dotnet trace collect --profile cpu-sampling -p <pid>
          PerfView:      PerfView /KernelEvents=default /ClrEvents=default collect

        Options:
          -n, --top <N>            Top N methods / call roots to display (default: 20)
          --process <name|pid>     Filter to a specific process name or PID
          --show-system            Include system/kernel frames (ntoskrnl, webengine4, iiscore, etc.)
          --show-unresolved        Include unresolved frames (<unresolved>, <managed, no symbols>)
                                   By default these frames are hidden.
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective cpu-trace app.nettrace
                    DumpDetective cpu-trace perf.etl --top 40 --process w3wp
          DumpDetective cpu-trace app.nettrace --output cpu-report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a       = CliArgs.Parse(args);
        int top     = a.GetInt("top", 20);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");
        bool filterSystem     = !a.HasFlag("show-system");
        bool filterUnresolved = !a.HasFlag("show-unresolved");

        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace or .etl).");
            AnsiConsole.Write(new Text(Help + "\n"));
            return 1;
        }

        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return 1;
        }

        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .nettrace or .etl — got: {Markup.Escape(Path.GetFileName(tracePath))}");
            return 1;
        }

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath))}");

            CpuTraceData? data = null;
            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            try
            {
                CommandBase.RunStatus(Name, update =>
                    data = _analyzer.Analyze(trace!, Path.GetFileName(tracePath!), top, processFilter, filterSystem, filterUnresolved, s => update($"{Name}  {s}")));
            }
            finally
            {
                trace?.Dispose();
            }

            _report.Render(data!, sink, top);

            foreach (var p in outputPaths)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");
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
            "cpu-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
