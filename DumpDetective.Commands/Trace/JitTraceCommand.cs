using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using Spectre.Console;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

public sealed class JitTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly JitTraceAnalyzer _analyzer;
    private readonly JitTraceReport   _report;

    public JitTraceCommand(JitTraceAnalyzer analyzer, JitTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "jit-trace";
    public string Description        => "JIT compilation analysis from a .nettrace or .etl trace (methods compiled, JIT time, hot modules).";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "JIT Trace";

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
        Usage: DumpDetective jit-trace <trace-file> [options]

        Parses JIT compilation events (Method/JittingStarted, Method/LoadVerbose) to report:
          • Total methods JIT-compiled and total compilation time
          • Methods that took longest to compile (complex generics, large methods)
          • Methods compiled multiple times (dynamic methods, Expression.Compile, ReJIT)
          • Module-level JIT summary (candidates for ReadyToRun pre-compilation)

        Collecting a JIT trace:
          dotnet-trace:  dotnet trace collect --profile startup -p <pid>
          dotnet-trace:  dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5' -p <pid>
          PerfView:      Enable 'JITStats' or 'Method' events in collection options

        Options:
          --top <N>            Top N methods to show (default: 30)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective jit-trace app.nettrace
          DumpDetective jit-trace startup.etl --process w3wp --top 50
          DumpDetective jit-trace app.nettrace --output jit-report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 30);
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

            JitTraceData? data = null;
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
            "jit-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
