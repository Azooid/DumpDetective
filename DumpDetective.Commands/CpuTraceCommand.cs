using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

namespace DumpDetective.Commands;

public sealed class CpuTraceCommand : ICommand
{
    private readonly CpuTraceAnalyzer _analyzer;
    private readonly CpuTraceReport   _report;

    public CpuTraceCommand(CpuTraceAnalyzer analyzer, CpuTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "cpu-trace";
    public string Description        => "CPU hot-path analysis from a .nettrace, .etl, or .etl.zip trace file (call tree + hot path, VS-style).";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp

    private const string Help = """
        Usage: DumpDetective cpu-trace <trace-file> [options]

        Parses CPU sampling events and produces:
          • Hot path  — the deepest chain of maximum CPU consumption (Visual Studio style)
          • Top methods by exclusive CPU time (the methods actually executing)
          • Call tree — inclusive/exclusive breakdown per caller → callee chain

        Supported input formats:
          .nettrace    EventPipe trace collected with --profile cpu-sampling
          .etl         Windows ETW trace with kernel CPU sampling
          .etl.zip     Compressed ETW trace (PerfView output)

        Collecting a CPU trace:
          dotnet-trace:  dotnet trace collect --profile cpu-sampling -p <pid>
          PerfView:      PerfView /KernelEvents=default /ClrEvents=default collect

        Options:
          -n, --top <N>            Top N methods / call roots to display (default: 20)
          --process <name|pid>     Filter to a specific process name or PID
          --show-system            Include system/kernel frames (ntoskrnl, webengine4, iiscore, etc.)
                                   By default these frames are hidden.
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective cpu-trace app.nettrace
          DumpDetective cpu-trace perf.etl.zip --top 40 --process w3wp
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

        using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0 ? a.EffectiveOutputPaths : null);
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath))}");

            CpuTraceData? data = null;
            CommandBase.RunStatus($"Parsing CPU samples...", update =>
                data = _analyzer.Analyze(tracePath, top, processFilter, filterSystem));

            _report.Render(data!, sink, top);

            foreach (var p in a.EffectiveOutputPaths.Where(p => !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
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
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "cpu-trace requires a trace file (.nettrace, .etl, or .etl.zip) — it cannot analyze a memory dump.");
}
