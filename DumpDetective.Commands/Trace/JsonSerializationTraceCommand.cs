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

public sealed class JsonSerializationTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly JsonSerializationTraceAnalyzer _analyzer;
    private readonly JsonSerializationTraceReport   _report;

    public JsonSerializationTraceCommand(JsonSerializationTraceAnalyzer analyzer,
                                         JsonSerializationTraceReport   report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "json-trace";
    public string Description        => "JSON serialization cost analysis — CPU time and allocation pressure from System.Text.Json, Newtonsoft.Json, and DataContract JSON.";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp
    public string Key                  => Name;
    public string SectionTitle         => "JSON Serialization";

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
        Usage: DumpDetective json-trace <trace-file> [options]

        Analyzes two ETW signals to measure JSON serialization cost:

          Allocation (GCAllocationTick — sampled every ~100 KB):
            • Types from System.Text.Json.*, Newtonsoft.Json.*, System.Runtime.Serialization.Json.*
            • Identifies which JSON types allocate the most memory

          CPU samples (SampledProfile / PerfInfo):
            • Frames with System.Text.Json, Newtonsoft.Json, or JsonSerializer names
            • Reports the % of CPU time spent inside JSON serialization / deserialization

          Top callers:
            • User-code frames directly above JSON library frames in call stacks
            • Shows which of your code triggers JSON work most frequently

        Collecting a suitable trace:
          dotnet-trace (CPU + allocation):
            dotnet trace collect --profile cpu-sampling \
              --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>

          dotnet-trace (allocation only):
            dotnet trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' -p <pid>

          PerfView:
            PerfView.exe /ClrEvents:GC,Type,GCHeapAndTypeNames,Default /KernelEvents:Profile /NoGui collect

        Options:
          --top <N>            Top N types / callers to show (default: 20)
          --process <name>     Filter to a specific process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective json-trace app.nettrace
          DumpDetective json-trace perf.etl --process w3wp --top 30
          DumpDetective json-trace app.nettrace --output json-cost.html
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

            TraceLog? trace = null;
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            Core.Models.CommandData.JsonSerializationTraceData? data = null;
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
            "json-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
