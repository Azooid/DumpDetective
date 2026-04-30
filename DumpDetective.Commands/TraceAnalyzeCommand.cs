using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Trace.Analyzers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;

namespace DumpDetective.Commands;

/// <summary>
/// Opens a trace file once and runs all six trace sub-analyzers sequentially,
/// producing a single combined report with one chapter per analyzer.
///
/// Sub-analyzers: cpu-trace, alloc-trace, gc-trace, contention-trace,
///                exceptions-trace, thread-pool-starvation
/// </summary>
public sealed class TraceAnalyzeCommand : ICommand
{
    private readonly CpuTraceAnalyzer              _cpu;
    private readonly AllocTraceAnalyzer            _alloc;
    private readonly GcTraceAnalyzer               _gc;
    private readonly ContentionTraceAnalyzer       _contention;
    private readonly ExceptionsTraceAnalyzer       _exceptions;
    private readonly ThreadPoolStarvationAnalyzer  _starvation;

    private readonly CpuTraceReport              _cpuReport;
    private readonly AllocTraceReport            _allocReport;
    private readonly GcTraceReport               _gcReport;
    private readonly ContentionTraceReport       _contentionReport;
    private readonly ExceptionsTraceReport       _exceptionsReport;
    private readonly ThreadPoolStarvationReport  _starvationReport;

    public TraceAnalyzeCommand(
        CpuTraceAnalyzer             cpu,             CpuTraceReport              cpuReport,
        AllocTraceAnalyzer           alloc,           AllocTraceReport            allocReport,
        GcTraceAnalyzer              gc,              GcTraceReport               gcReport,
        ContentionTraceAnalyzer      contention,      ContentionTraceReport       contentionReport,
        ExceptionsTraceAnalyzer      exceptions,      ExceptionsTraceReport       exceptionsReport,
        ThreadPoolStarvationAnalyzer starvation,      ThreadPoolStarvationReport  starvationReport)
    {
        _cpu = cpu;           _cpuReport = cpuReport;
        _alloc = alloc;       _allocReport = allocReport;
        _gc = gc;             _gcReport = gcReport;
        _contention = contention; _contentionReport = contentionReport;
        _exceptions = exceptions; _exceptionsReport = exceptionsReport;
        _starvation = starvation; _starvationReport = starvationReport;
    }

    public string Name               => "trace-analyze";
    public string Description        => "Full trace analysis — opens trace once and runs all sub-analyzers (cpu, alloc, gc, contention, exceptions, thread-pool-starvation).";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp

    private const string Help = """
        Usage: DumpDetective trace-analyze <trace-file> [options]

        Opens the trace file ONCE and runs all six trace sub-analyzers in sequence,
        producing a single combined report:

          • cpu-trace             CPU hot-path, top methods, call tree
          • alloc-trace           Top allocating types and call sites
          • gc-trace              GC pause times, generations, trigger reasons
          • contention-trace      Lock contention hotspots and wait times
          • exceptions-trace      Exception flood patterns by type
          • thread-pool-starvation  ThreadPool starvation signals and adjustments

        Supported input formats:
          .nettrace    EventPipe trace collected with a suitable profile
          .etl         Windows ETW trace
          .etl.zip     Compressed ETW trace (PerfView output)

        Options:
          -n, --top <N>            Top N items per section (default: 20)
          --process <name>         Filter to a specific process name
          --show-system            Include system/kernel frames in CPU tree (default: hidden)
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective trace-analyze app.nettrace
          DumpDetective trace-analyze perf.etl.zip --process w3wp --output report.html
          DumpDetective trace-analyze app.nettrace --top 30 --show-system
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 20);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");
        bool    filterSystem  = !a.HasFlag("show-system");

        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace, .etl, or .etl.zip).");
            AnsiConsole.MarkupLine(Markup.Escape(Help));
            return 1;
        }
        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return 1;
        }
        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .nettrace, .etl, or .etl.zip — got: {Markup.Escape(Path.GetFileName(tracePath))}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath))}");

        TraceLog? trace = null;
        try
        {
            CommandBase.RunStatus("Opening trace file...", _ =>
                trace = TraceLog.OpenOrConvert(tracePath,
                    new TraceLogOptions { ConversionLog = TextWriter.Null }));

            string traceFileName = Path.GetFileName(tracePath);
            using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0
                ? a.EffectiveOutputPaths : null);

            sink.Header("Trace Analysis",
                $"File: {traceFileName}" +
                (processFilter is not null ? $" | Process: {processFilter}" : ""),
                navLevel: 1);

            RunAnalyzer("cpu-trace", () =>
            {
                sink.Header("CPU Trace", traceFileName, navLevel: 2, commandName: "cpu-trace");
                var data = _cpu.Analyze(trace!, traceFileName, top, processFilter, filterSystem);
                _cpuReport.Render(data, sink, top);
            });
            RunAnalyzer("alloc-trace", () =>
            {
                sink.Header("Allocation Trace", traceFileName, navLevel: 2, commandName: "alloc-trace");
                var data = _alloc.Analyze(trace!, traceFileName, top, processFilter);
                _allocReport.Render(data, sink, top);
            });
            RunAnalyzer("gc-trace", () =>
            {
                sink.Header("GC Trace", traceFileName, navLevel: 2, commandName: "gc-trace");
                var data = _gc.Analyze(trace!, traceFileName, top, processFilter);
                _gcReport.Render(data, sink, top);
            });
            RunAnalyzer("contention-trace", () =>
            {
                sink.Header("Contention Trace", traceFileName, navLevel: 2, commandName: "contention-trace");
                var data = _contention.Analyze(trace!, traceFileName, top, processFilter);
                _contentionReport.Render(data, sink, top);
            });
            RunAnalyzer("exceptions-trace", () =>
            {
                sink.Header("Exceptions Trace", traceFileName, navLevel: 2, commandName: "exceptions-trace");
                var data = _exceptions.Analyze(trace!, traceFileName, top, processFilter);
                _exceptionsReport.Render(data, sink, top);
            });
            RunAnalyzer("thread-pool-starvation", () =>
            {
                sink.Header("Thread Pool Starvation", traceFileName, navLevel: 2, commandName: "thread-pool-starvation");
                var data = _starvation.Analyze(trace!, traceFileName, top);
                _starvationReport.Render(data, sink, top);
            });

            foreach (var p in a.EffectiveOutputPaths.Where(p =>
                !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(p)}");
            if (a.EffectiveOutputPaths.Count == 0 && sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    private static void RunAnalyzer(string name, Action run)
    {
        try
        {
            CommandBase.RunStatus($"Running {name}...", _ => run());
            AnsiConsole.MarkupLine($"  [green]✓[/] {name}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] {name} failed: {Markup.Escape(ex.Message)}");
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "trace-analyze requires a trace file (.nettrace, .etl, or .etl.zip) — it cannot analyze a memory dump.");
}
