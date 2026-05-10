using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class ProcessLifecycleCommand : ICommand
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

        if (!GcTraceCommand.ValidateTrace(tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            Core.Models.CommandData.ProcessLifecycleData? data = null;
            CommandBase.RunStatus("Parsing process lifecycle events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

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
