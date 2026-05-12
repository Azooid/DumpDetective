using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class ProcessLifecycleCommand : ICommand, ITraceSubAnalyzer
{
    private readonly ProcessLifecycleAnalyzer _analyzer;
    private readonly ProcessLifecycleReport   _report;

    public ProcessLifecycleCommand(ProcessLifecycleAnalyzer analyzer, ProcessLifecycleReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "process-lifecycle-trace";
    public string Description        => "Process lifecycle analysis — detects crashes, restarts, and abnormal exits from Process/Start/Stop events.";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "Process Lifecycle";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, p.ProcessFilter, progress);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective process-lifecycle-trace <trace-file> [options]

        Analyzes Process/Start and Process/Stop events to detect:
          • Process restarts (stop followed by start within 30 seconds)
          • Abnormal exits (non-zero exit code)
          • Process churn by process name

        Available in ETL traces (kernel Process provider).
        PerfView: enable 'KernelProcess'. xperf: -on PROC_THREAD

        Options:
          --top <N>            Max process groups to show (default: 20)
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

        if (!GcTraceCommand.ValidateTrace(ref tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            Core.Models.CommandData.ProcessLifecycleData? data = null;
            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            try
            {
                CommandBase.RunStatus(Name, update =>
                    data = _analyzer.Analyze(trace!, Path.GetFileName(tracePath!), top, processFilter, s => update($"{Name}  {s}")));
            }
            finally
            {
                trace?.Dispose();
            }

            sink.Header("Process Lifecycle Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "process-lifecycle-trace");
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
            "process-lifecycle-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
