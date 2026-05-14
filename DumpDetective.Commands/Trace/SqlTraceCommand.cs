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

public sealed class SqlTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly SqlTraceAnalyzer _analyzer;
    private readonly SqlTraceReport   _report;

    public SqlTraceCommand(SqlTraceAnalyzer analyzer, SqlTraceReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "sql-trace";
    public string Description        => "SQL/database command analysis — query durations, slow queries, error rates from SqlClient and EF Core event sources.";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "SQL & Network";
    public CommandKind Kind               => CommandKind.Trace;
    public string Key                  => Name;
    public string SectionTitle         => "SQL / EF Trace";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _analyzer.Analyze(trace, traceFileName, p.Top, p.ProcessFilter, p.SlowMs, progress);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    public bool SupportsConsumer => true;

    public ITraceEventConsumer? CreateConsumer(TraceRunParams p, string traceFileName)
        => _analyzer.CreateConsumer(p.ProcessFilter, p.SlowMs);

    public string? CompleteFromConsumer(ITraceEventConsumer consumer, string traceFileName,
        TraceRunParams p, Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var d = _analyzer.BuildResult(consumer, traceFileName, p.Top, p.ProcessFilter);
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective sql-trace <trace-file> [options]

        Parses Microsoft.Data.SqlClient, System.Data.SqlClient, and EF Core events to:
          • Report aggregate query execution time per pattern
          • Identify slow individual commands above --slow-ms threshold
          • Detect query errors and error-rate spikes

        Collecting a SQL trace:
          dotnet-trace:
            dotnet trace collect \
              --providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4,
                           System.Data.SqlClient.EventSource:0xFF:4,
                           Microsoft-EntityFrameworkCore:0xFFFF:5' -p <pid>

          PerfView:
            /Providers:"Microsoft.Data.SqlClient.EventSource,Microsoft-EntityFrameworkCore"

        Query selection (three tiers, up to 170 unique patterns):
          Tier 1 — top 100 query patterns by cumulative total time
          Tier 2 — top 50 query patterns by worst single-execution time (surfaces one-off slow outliers)
          Tier 3 — up to 20 additional unique patterns with real SQL text not already in tiers 1–2
          All tiers are merged, deduplicated, and sorted: real SQL text first (total ↓, max ↓),
          then (no SQL text) placeholders at the bottom.

        Options:
          --top <N>            Tier-1 row cap (default: 100; tier-2 = N/2; 0 = unlimited)
          --process <name>     Filter to a specific process name
          --slow-ms <ms>       Slow command threshold in ms (default: 500)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective sql-trace app.nettrace
          DumpDetective sql-trace perf.etl --process w3wp --slow-ms 200
          DumpDetective sql-trace app.nettrace --output sql.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 100);
        double  slowMs        = a.GetInt("slow-ms", 500);
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

            Core.Models.CommandData.SqlTraceData? data = null;
            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            try
            {
                CommandBase.RunStatus(Name, update =>
                    data = _analyzer.Analyze(trace!, Path.GetFileName(tracePath!), top, processFilter, slowMs, s => update($"{Name}  {s}")));
            }
            finally
            {
                trace?.Dispose();
            }

            sink.Header("SQL Trace", Path.GetFileName(tracePath!), navLevel: 2, commandName: "sql-trace");
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
            "sql-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
