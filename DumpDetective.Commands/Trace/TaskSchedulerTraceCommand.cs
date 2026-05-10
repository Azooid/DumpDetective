using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class TaskSchedulerTraceCommand : ICommand
{
    private readonly TaskSchedulerTraceAnalyzer _analyzer;
    private readonly TaskSchedulerTraceReport   _report;

    public TaskSchedulerTraceCommand(TaskSchedulerTraceAnalyzer analyzer, TaskSchedulerTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "task-scheduler-trace";
    public string Description        => "Task Scheduler analysis — detects long-running tasks, cancelled tasks, and excessive wait depth from Task events.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective task-scheduler-trace <trace-file> [options]

        Analyzes TaskScheduled, Task/Execute, and TaskWait events to detect:
          • Long-running tasks (>5 seconds, holding ThreadPool threads)
          • Cancelled tasks
          • Deep task wait chains

        Collect with:
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x40:4' (ThreadingKeyword)

        Options:
          --top <N>            Max long-running tasks to show (default: 20)
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

            Core.Models.CommandData.TaskSchedulerTraceData? data = null;
            CommandBase.RunStatus("Parsing task scheduler events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Task Scheduler Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "task-scheduler-trace");
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
            "task-scheduler-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
