using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;
using CmdData = DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Runs prerequisite analyzers (cpu, gc, alloc-burst, contention, exceptions)
/// then feeds their timelines into AnomalyDetectionAnalyzer for z-score analysis.
/// </summary>
public sealed class AnomalyDetectionCommand : ICommand, ITraceSubAnalyzer
{
    private readonly CpuTraceAnalyzer          _cpu;
    private readonly GcTraceAnalyzer           _gc;
    private readonly AllocationBurstAnalyzer   _allocBurst;
    private readonly ContentionTraceAnalyzer   _contention;
    private readonly ExceptionsTraceAnalyzer   _exceptions;
    private readonly AnomalyDetectionAnalyzer  _anomaly;
    private readonly AnomalyDetectionReport    _report;

    public AnomalyDetectionCommand(
        CpuTraceAnalyzer        cpu,
        GcTraceAnalyzer         gc,
        AllocationBurstAnalyzer allocBurst,
        ContentionTraceAnalyzer contention,
        ExceptionsTraceAnalyzer exceptions,
        AnomalyDetectionAnalyzer anomaly,
        AnomalyDetectionReport  report)
    {
        _cpu        = cpu;
        _gc         = gc;
        _allocBurst = allocBurst;
        _contention = contention;
        _exceptions = exceptions;
        _anomaly    = anomaly;
        _report     = report;
    }

    public string Name               => "anomaly-trace";
    public string Description        => "Statistical anomaly detection — z-score analysis over CPU, GC, allocation, contention, and exception rate timelines.";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "Anomaly Detection";

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _anomaly.Analyze(traceFileName, p.ProcessFilter,
            cpu:        results.GetValueOrDefault("cpu-trace")         as CpuTraceData,
            gc:         results.GetValueOrDefault("gc-trace")          as GcTraceData,
            alloc:      results.GetValueOrDefault("alloc-burst-trace") as AllocationBurstData,
            contention: results.GetValueOrDefault("contention-trace")  as ContentionTraceData,
            exceptions: results.GetValueOrDefault("exceptions-trace")  as ExceptionsTraceData);
        _report.Render(d, sink, p.Top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective anomaly-trace <trace-file> [options]

        Runs CPU, GC, allocation burst, contention, and exception analyzers, then
        applies a rolling z-score (window=10s, threshold=3.0) over their timelines.

        Any point more than 3σ above the rolling baseline is flagged as an anomaly.

        Collect with (recommended full set):
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:5,System.Runtime:0xFF:4'

        Options:
          --process <name>     Filter to process name
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");

        if (!GcTraceCommand.ValidateTrace(tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);

        TraceLog? trace = null;
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            CommandBase.RunStatus("Opening trace file...", _ =>
                trace = TraceLog.OpenOrConvert(tracePath!,
                    new TraceLogOptions { ConversionLog = TextWriter.Null }));

            string fileName = Path.GetFileName(tracePath!);

            CmdData.CpuTraceData?        cpuData    = null;
            CmdData.GcTraceData?         gcData     = null;
            CmdData.AllocationBurstData? allocData  = null;
            CmdData.ContentionTraceData? contData   = null;
            CmdData.ExceptionsTraceData? excData    = null;

            CommandBase.RunStatus("Collecting metric timelines...", _ =>
            {
                cpuData   = _cpu.Analyze(trace!, fileName, 20, processFilter);
                gcData    = _gc.Analyze(trace!, fileName, 20, processFilter);
                allocData = _allocBurst.Analyze(trace!, fileName, 20, processFilter);
                contData  = _contention.Analyze(trace!, fileName, 20, processFilter);
                excData   = _exceptions.Analyze(trace!, fileName, 20, processFilter);
            });

            CmdData.AnomalyDetectionData? data = null;
            CommandBase.RunStatus("Running z-score anomaly detection...", _ =>
                data = _anomaly.Analyze(fileName, processFilter,
                    cpu: cpuData, gc: gcData, alloc: allocData,
                    contention: contData, exceptions: excData));

            sink.Header("Anomaly Detection", fileName, navLevel: 2, commandName: "anomaly-trace");
            _report.Render(data!, sink);
            GcTraceCommand.PrintOutputPath(outputPaths);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "anomaly-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
