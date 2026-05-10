using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands.Trace;

public sealed class FileIoTraceCommand : ICommand
{
    private readonly FileIoTraceAnalyzer _analyzer;
    private readonly FileIoTraceReport   _report;

    public FileIoTraceCommand(FileIoTraceAnalyzer analyzer, FileIoTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "file-io-trace";
    public string Description        => "File I/O analysis — detects slow synchronous reads/writes and high-throughput files from kernel file events (ETL only).";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective file-io-trace <trace-file.etl> [options]

        Analyzes Microsoft-Windows-Kernel-File provider events to detect:
          • Slow synchronous file operations (>10 ms)
          • Top files by total bytes transferred
          • I/O throughput over time

        Note: Kernel file I/O events are only in ETL traces, not .nettrace.

        Collect with:
          PerfView: enable 'FileIOReadWrite' in collection dialog
          xperf:    -on PROC_THREAD+LOADER+FileIO

        Options:
          --top <N>            Max files/operations to show (default: 20)
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

            Core.Models.CommandData.FileIoTraceData? data = null;
            CommandBase.RunStatus("Parsing file I/O events...", _ =>
                data = _analyzer.Analyze(tracePath!, top, processFilter));

            sink.Header("File I/O Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "file-io-trace");
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
            "file-io-trace requires an ETL trace file — it cannot analyze a memory dump.");
}
