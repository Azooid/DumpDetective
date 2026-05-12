using DumpDetective.Analysis.Trace;
using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using DumpDetective.Core.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;
using CmdData = DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Runs all trace analyzers and synthesizes ranked causal chains from their outputs
/// plus CorrelationEngine findings.
/// </summary>
public sealed class RootCauseTraceCommand : ICommand, ITraceSubAnalyzer
{
    private readonly CpuTraceAnalyzer            _cpu;
    private readonly AllocTraceAnalyzer          _alloc;
    private readonly GcTraceAnalyzer             _gc;
    private readonly ContentionTraceAnalyzer     _contention;
    private readonly ExceptionsTraceAnalyzer     _exceptions;
    private readonly ThreadPoolStarvationAnalyzer _starvation;
    private readonly JitTraceAnalyzer            _jit;
    private readonly HttpTraceAnalyzer           _http;
    private readonly AsyncTraceAnalyzer          _async;
    private readonly SqlTraceAnalyzer            _sql;
    private readonly FinalizerTraceAnalyzer      _finalizer;
    private readonly ConnectionPoolTraceAnalyzer _connPool;
    private readonly AllocationBurstAnalyzer     _allocBurst;
    private readonly DeadlockPatternAnalyzer     _deadlock;
    private readonly LohTraceAnalyzer            _loh;
    private readonly RetryStormAnalyzer          _retryStorm;
    private readonly RootCauseChainAnalyzer      _rootCause;
    private readonly RootCauseChainReport        _report;

    public RootCauseTraceCommand(
        CpuTraceAnalyzer            cpu,
        AllocTraceAnalyzer          alloc,
        GcTraceAnalyzer             gc,
        ContentionTraceAnalyzer     contention,
        ExceptionsTraceAnalyzer     exceptions,
        ThreadPoolStarvationAnalyzer starvation,
        JitTraceAnalyzer            jit,
        HttpTraceAnalyzer           http,
        AsyncTraceAnalyzer          async_,
        SqlTraceAnalyzer            sql,
        FinalizerTraceAnalyzer      finalizer,
        ConnectionPoolTraceAnalyzer connPool,
        AllocationBurstAnalyzer     allocBurst,
        DeadlockPatternAnalyzer     deadlock,
        LohTraceAnalyzer            loh,
        RetryStormAnalyzer          retryStorm,
        RootCauseChainAnalyzer      rootCause,
        RootCauseChainReport        report)
    {
        _cpu        = cpu;
        _alloc      = alloc;
        _gc         = gc;
        _contention = contention;
        _exceptions = exceptions;
        _starvation = starvation;
        _jit        = jit;
        _http       = http;
        _async      = async_;
        _sql        = sql;
        _finalizer  = finalizer;
        _connPool   = connPool;
        _allocBurst = allocBurst;
        _deadlock   = deadlock;
        _loh        = loh;
        _retryStorm = retryStorm;
        _rootCause  = rootCause;
        _report     = report;
    }

    public string Name               => "root-cause-trace";
    public string Description        => "Root cause chain synthesis — runs all trace analyzers and derives ranked causal chains with actionable remediation advice.";
    public bool   IncludeInFullAnalyze => false;
    public string Key                  => Name;
    public string SectionTitle         => "Root Cause Analysis";
    public bool   HasCorrelationPhase  => true;

    public string? Run(TraceLog trace, string traceFileName, TraceRunParams p,
                       Dictionary<string, ReportDoc> captured, Dictionary<string, object?> results,
                       Action<string>? progress = null) => null;

    public string? OnCorrelationAvailable(string traceFileName, IReadOnlyList<CorrelationFinding> findings,
                                          Dictionary<string, ReportDoc> captured,
                                          Dictionary<string, object?> results, int top)
    {
        var sink = new CaptureSink();
        sink.Header(SectionTitle, traceFileName, navLevel: 3, commandName: Name);
        var d = _rootCause.Analyze(traceFileName,
            correlations:  findings,
            cpu:           results.GetValueOrDefault("cpu-trace")              as CpuTraceData,
            alloc:         results.GetValueOrDefault("alloc-trace")            as AllocTraceData,
            gc:            results.GetValueOrDefault("gc-trace")               as GcTraceData,
            contention:    results.GetValueOrDefault("contention-trace")       as ContentionTraceData,
            exceptions:    results.GetValueOrDefault("exceptions-trace")       as ExceptionsTraceData,
            starvation:    results.GetValueOrDefault("threadpool-starvation")  as ThreadPoolStarvationData,
            jit:           results.GetValueOrDefault("jit-trace")              as JitTraceData,
            http:          results.GetValueOrDefault("http-trace")             as HttpTraceData,
            async_:        results.GetValueOrDefault("async-trace")            as AsyncTraceData,
            sql:           results.GetValueOrDefault("sql-trace")              as SqlTraceData,
            finalizer:     results.GetValueOrDefault("finalizer-trace")        as FinalizerTraceData,
            connPool:      results.GetValueOrDefault("connection-pool-trace")  as ConnectionPoolTraceData,
            allocBurst:    results.GetValueOrDefault("alloc-burst-trace")      as AllocationBurstData,
            deadlock:      results.GetValueOrDefault("deadlock-trace")         as DeadlockPatternData,
            loh:           results.GetValueOrDefault("loh-trace")              as LohTraceData,
            retryStorm:    results.GetValueOrDefault("retry-storm-trace")      as RetryStormData,
            taskScheduler: results.GetValueOrDefault("task-scheduler-trace")   as TaskSchedulerTraceData,
            fileIo:        results.GetValueOrDefault("file-io-trace")          as FileIoTraceData,
            socket:        results.GetValueOrDefault("socket-trace")           as SocketTraceData,
            dns:           results.GetValueOrDefault("dns-trace")              as DnsTraceData,
            anomaly:       results.GetValueOrDefault("anomaly-trace")          as AnomalyDetectionData);
        _report.Render(d, sink, top);
        captured[Name] = sink.GetDoc(); results[Name] = d;
        return d.TraceInfo;
    }

    private const string Help = """
        Usage: DumpDetective root-cause-trace <trace-file> [options]

        Runs all trace analyzers, feeds their outputs through CorrelationEngine,
        and synthesizes ranked causal chains with:
          • Root cause identification
          • Downstream effect listing
          • Evidence summary
          • Actionable remediation advice

        Collect with (recommended full set):
          dotnet-trace: --providers 'Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:5,
                         System.Net.Sockets:0xFF:5,System.Net.NameResolution:0xFF:5,
                         Microsoft.Data.SqlClient.EventSource:0xFF:5'

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

        if (!GcTraceCommand.ValidateTrace(ref tracePath, Help)) return 1;

        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
        using var sink = SinkFactory.CreateMulti(outputPaths);

        TraceLog? trace = null;
        try
        {
            if (!CommandBase.SuppressVerbose)
                AnsiConsole.MarkupLine($"[bold]Analyzing:[/] {Markup.Escape(Path.GetFileName(tracePath!))}");

            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath!, s => update($"{Name}  {s}")));

            string fileName = Path.GetFileName(tracePath!);

            CmdData.CpuTraceData?             cpuData       = null;
            CmdData.AllocTraceData?           allocData     = null;
            CmdData.GcTraceData?              gcData        = null;
            CmdData.ContentionTraceData?      contData      = null;
            CmdData.ExceptionsTraceData?      excData       = null;
            CmdData.ThreadPoolStarvationData? starvData     = null;
            CmdData.JitTraceData?             jitData       = null;
            CmdData.HttpTraceData?            httpData      = null;
            CmdData.AsyncTraceData?           asyncData     = null;
            CmdData.SqlTraceData?             sqlData       = null;
            CmdData.FinalizerTraceData?       finData       = null;
            CmdData.ConnectionPoolTraceData?  connData      = null;
            CmdData.AllocationBurstData?      burstData     = null;
            CmdData.DeadlockPatternData?      deadlockData  = null;
            CmdData.LohTraceData?             lohData       = null;
            CmdData.RetryStormData?           retryData     = null;

            int n = 20;
            CommandBase.RunStatus("Running all trace analyzers...", _ =>
            {
                cpuData      = _cpu.Analyze(trace!, fileName, n, processFilter);
                allocData    = _alloc.Analyze(trace!, fileName, n, processFilter);
                gcData       = _gc.Analyze(trace!, fileName, n, processFilter);
                contData     = _contention.Analyze(trace!, fileName, n, processFilter);
                excData      = _exceptions.Analyze(trace!, fileName, n, processFilter);
                starvData    = _starvation.Analyze(trace!, fileName, n);
                jitData      = _jit.Analyze(trace!, fileName, n, processFilter);
                httpData     = _http.Analyze(trace!, fileName, n, processFilter);
                asyncData    = _async.Analyze(trace!, fileName, n, processFilter);
                sqlData      = _sql.Analyze(trace!, fileName, n, processFilter);
                finData      = _finalizer.Analyze(trace!, fileName, n, processFilter);
                connData     = _connPool.Analyze(trace!, fileName, n, processFilter);
                burstData    = _allocBurst.Analyze(trace!, fileName, n, processFilter);
                deadlockData = _deadlock.Analyze(trace!, fileName, n, processFilter);
                lohData      = _loh.Analyze(trace!, fileName, n, processFilter);
                retryData    = _retryStorm.Analyze(trace!, fileName, n, processFilter);
            });

            CmdData.RootCauseChainData? data = null;
            CommandBase.RunStatus("Synthesizing root cause chains...", _ =>
            {
                var correlations = CorrelationEngine.Correlate(
                    cpuData, allocData, gcData, contData, excData,
                    starvData, jitData, httpData, asyncData, sqlData);

                data = _rootCause.Analyze(
                    fileName,
                    correlations:   correlations,
                    cpu:            cpuData,
                    alloc:          allocData,
                    gc:             gcData,
                    contention:     contData,
                    exceptions:     excData,
                    starvation:     starvData,
                    jit:            jitData,
                    http:           httpData,
                    async_:         asyncData,
                    sql:            sqlData,
                    finalizer:      finData,
                    connPool:       connData,
                    allocBurst:     burstData,
                    deadlock:       deadlockData,
                    loh:            lohData,
                    retryStorm:     retryData);
            });

            sink.Header("Root Cause Analysis", fileName, navLevel: 2, commandName: "root-cause-trace");
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
            "root-cause-trace requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");
}
