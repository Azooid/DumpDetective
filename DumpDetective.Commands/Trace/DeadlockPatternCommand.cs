using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class DeadlockPatternCommand : ICommand
{
    private readonly DeadlockPatternAnalyzer _analyzer;
    private readonly DeadlockPatternReport   _report;

    public DeadlockPatternCommand(DeadlockPatternAnalyzer analyzer, DeadlockPatternReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "deadlock-trace";
    public string Description        => "Deadlock pattern detection — heuristic detection of mutually-blocked thread pairs from contention and wait events.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective deadlock-trace <trace-file> [options]

        Uses ContentionStart and WaitHandleWaitStart events to heuristically detect
        thread pairs that remain mutually blocked for more than 5 seconds.

        Collect with:
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x300:5' (Contention + Threading)

        Options:
          --top <N>            Max wait chains to show (default: 20)
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

            Core.Models.CommandData.DeadlockPatternData? data = null;
            CommandBase.RunStatus("Detecting deadlock patterns...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("Deadlock Pattern Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "deadlock-trace");
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
            "deadlock-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
