using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class ContextSwitchTraceCommand : ICommand
{
    private readonly ContextSwitchTraceAnalyzer _analyzer;
    private readonly ContextSwitchTraceReport   _report;

    public ContextSwitchTraceCommand(ContextSwitchTraceAnalyzer analyzer,
                                     ContextSwitchTraceReport   report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "context-switch-trace";
    public string Description        => "Kernel context-switch analysis — thread scheduling frequency, voluntary vs preempted splits, and wait-reason breakdown from CSwitch events.";
    public bool   IncludeInFullAnalyze => false; // requires a trace with kernel events

    private const string Help = """
        Usage: DumpDetective context-switch-trace <trace-file> [options]

        Analyzes Windows kernel CSwitch (context switch) events to reveal:
          • Thread scheduling frequency and CPU run-slice length
          • Voluntary vs preempted split — voluntary = blocked on I/O/lock/timer,
            preempted = CPU quantum expired or higher-priority thread ready
          • Wait-reason distribution — what threads block on most frequently
            (lock/mutex contention, thread-pool queue, timers, page faults, COM/RPC)
          • Top threads by switch count — identify spinning or chatty threads

        Note: CSwitch events are KERNEL events not captured by dotnet-trace (.nettrace).
        You must use PerfView or xperf to collect an ETL trace with kernel events.

        Collecting a trace with CSwitch events:
          PerfView:
            PerfView.exe /KernelEvents:ContextSwitch /ClrEvents:Default /NoGui collect
            # Or use /KernelEvents:Default which includes CSwitch + CPU sampling

          xperf:
            xperf -on PROC_THREAD+LOADER+CSWITCH -f kernel.etl
            # (collect, then stop and merge)
            xperf -stop
            xperf -merge kernel.etl merged.etl

        Combined with context-switch-trace:
          DumpDetective trace-analyze merged.etl --process w3wp

        Options:
          --top <N>            Top N threads to show (default: 30)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective context-switch-trace trace.etl
          DumpDetective context-switch-trace perf.etl --process w3wp --top 50
          DumpDetective context-switch-trace perf.etl --output cswitch.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 30);
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

            Core.Models.CommandData.ContextSwitchTraceData? data = null;
            CommandBase.RunStatus("Parsing CSwitch events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

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
            "context-switch-trace requires a kernel ETL trace — it cannot analyze a memory dump.");
}
