using DumpDetective.Analysis.Memory;
using DumpDetective.Analysis.Trace;
using DumpDetective.Analysis.Trace.Analyzers;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;
using CmdData = DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Combined trace + dump analysis.
///
/// Opens a trace file (performance data over time) and a memory dump (heap snapshot
/// captured during the same incident window), runs all trace sub-analyzers plus a
/// lightweight dump walk, then runs <see cref="TraceDumpCorrelator"/> to surface
/// cross-source findings — patterns that require BOTH sources to detect.
///
/// Typical workflow:
/// <code>
///   # 1. Start collecting a trace
///   dotnet-trace collect --profile cpu-sampling -o app.nettrace
///   # 2. While trace is running, capture a dump
///   dotnet-dump collect --process-id &lt;pid&gt; -o app.dmp
///   # 3. Stop the trace
///   dotnet-trace stop
///   # 4. Analyse both together
///   DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html
/// </code>
/// </summary>
public sealed class TraceDumpAnalyzeCommand : ICommand
{
    private readonly CpuTraceAnalyzer              _cpu;
    private readonly AllocTraceAnalyzer                 _alloc;
    private readonly GcTraceAnalyzer                    _gc;
    private readonly ContentionTraceAnalyzer            _contention;
    private readonly ExceptionsTraceAnalyzer            _exceptions;
    private readonly ThreadPoolStarvationAnalyzer       _starvation;
    private readonly JitTraceAnalyzer                   _jit;
    private readonly HttpTraceAnalyzer                  _http;
    private readonly AsyncTraceAnalyzer                 _async;
    private readonly SqlTraceAnalyzer                   _sql;
    private readonly JsonSerializationTraceAnalyzer     _json;
    private readonly ContextSwitchTraceAnalyzer         _cswitch;

    private readonly CpuTraceReport                     _cpuReport;
    private readonly AllocTraceReport                   _allocReport;
    private readonly GcTraceReport                      _gcReport;
    private readonly ContentionTraceReport              _contentionReport;
    private readonly ExceptionsTraceReport              _exceptionsReport;
    private readonly ThreadPoolStarvationReport         _starvationReport;
    private readonly JitTraceReport                     _jitReport;
    private readonly HttpTraceReport                    _httpReport;
    private readonly AsyncTraceReport                   _asyncReport;
    private readonly SqlTraceReport                     _sqlReport;
    private readonly JsonSerializationTraceReport       _jsonReport;
    private readonly ContextSwitchTraceReport         _cswitchReport;
    private readonly FinalizerTraceAnalyzer             _finalizer;
    private readonly ConnectionPoolTraceAnalyzer        _connPool;
    private readonly AllocationBurstAnalyzer            _allocBurst;
    private readonly DeadlockPatternAnalyzer            _deadlock;
    private readonly LohTraceAnalyzer                   _loh;
    private readonly RetryStormAnalyzer                 _retryStorm;
    private readonly ProcessLifecycleAnalyzer           _processLife;
    private readonly TaskSchedulerTraceAnalyzer         _taskSched;
    private readonly FileIoTraceAnalyzer                _fileIo;
    private readonly SocketTraceAnalyzer                _socket;
    private readonly DnsTraceAnalyzer                   _dns;
    private readonly KestrelTraceAnalyzer               _kestrel;
    private readonly HandleLeakTraceAnalyzer            _handleLeak;
    private readonly AspNetCorePipelineAnalyzer         _aspnet;
    private readonly OpenTelemetryTraceAnalyzer         _otel;
    private readonly AnomalyDetectionAnalyzer           _anomaly;
    private readonly RootCauseChainAnalyzer             _rootCause;
    private readonly FinalizerTraceReport               _finalizerReport;
    private readonly ConnectionPoolTraceReport          _connPoolReport;
    private readonly AllocationBurstReport              _allocBurstReport;
    private readonly DeadlockPatternReport              _deadlockReport;
    private readonly LohTraceReport                     _lohReport;
    private readonly RetryStormReport                   _retryStormReport;
    private readonly ProcessLifecycleReport             _processLifeReport;
    private readonly TaskSchedulerTraceReport           _taskSchedReport;
    private readonly FileIoTraceReport                  _fileIoReport;
    private readonly SocketTraceReport                  _socketReport;
    private readonly DnsTraceReport                     _dnsReport;
    private readonly KestrelTraceReport                 _kestrelReport;
    private readonly HandleLeakTraceReport              _handleLeakReport;
    private readonly AspNetCorePipelineReport           _aspnetReport;
    private readonly OpenTelemetryTraceReport           _otelReport;
    private readonly AnomalyDetectionReport             _anomalyReport;
    private readonly RootCauseChainReport               _rootCauseReport;
    private readonly TraceDumpCorrelationReport         _correlationReport;

    public TraceDumpAnalyzeCommand(
        CpuTraceAnalyzer                 cpu,        CpuTraceReport                   cpuReport,
        AllocTraceAnalyzer               alloc,      AllocTraceReport                 allocReport,
        GcTraceAnalyzer                  gc,         GcTraceReport                    gcReport,
        ContentionTraceAnalyzer          contention, ContentionTraceReport             contentionReport,
        ExceptionsTraceAnalyzer          exceptions, ExceptionsTraceReport             exceptionsReport,
        ThreadPoolStarvationAnalyzer     starvation, ThreadPoolStarvationReport        starvationReport,
        JitTraceAnalyzer                 jit,        JitTraceReport                   jitReport,
        HttpTraceAnalyzer                http,       HttpTraceReport                  httpReport,
        AsyncTraceAnalyzer               async_,     AsyncTraceReport                 asyncReport,
        SqlTraceAnalyzer                 sql,        SqlTraceReport                   sqlReport,
        JsonSerializationTraceAnalyzer   json,       JsonSerializationTraceReport     jsonReport,
        ContextSwitchTraceAnalyzer       cswitch,    ContextSwitchTraceReport         cswitchReport,
        FinalizerTraceAnalyzer           finalizer,      FinalizerTraceReport             finalizerReport,
        ConnectionPoolTraceAnalyzer      connPool,       ConnectionPoolTraceReport        connPoolReport,
        AllocationBurstAnalyzer          allocBurst,     AllocationBurstReport            allocBurstReport,
        DeadlockPatternAnalyzer          deadlock,       DeadlockPatternReport            deadlockReport,
        LohTraceAnalyzer                 loh,            LohTraceReport                   lohReport,
        RetryStormAnalyzer               retryStorm,     RetryStormReport                 retryStormReport,
        ProcessLifecycleAnalyzer         processLife,    ProcessLifecycleReport           processLifeReport,
        TaskSchedulerTraceAnalyzer       taskSched,      TaskSchedulerTraceReport         taskSchedReport,
        FileIoTraceAnalyzer              fileIo,         FileIoTraceReport                fileIoReport,
        SocketTraceAnalyzer              socket,         SocketTraceReport                socketReport,
        DnsTraceAnalyzer                 dns,            DnsTraceReport                   dnsReport,
        KestrelTraceAnalyzer             kestrel,        KestrelTraceReport               kestrelReport,
        HandleLeakTraceAnalyzer          handleLeak,     HandleLeakTraceReport            handleLeakReport,
        AspNetCorePipelineAnalyzer       aspnet,         AspNetCorePipelineReport         aspnetReport,
        OpenTelemetryTraceAnalyzer       otel,           OpenTelemetryTraceReport         otelReport,
        AnomalyDetectionAnalyzer         anomaly,        AnomalyDetectionReport           anomalyReport,
        RootCauseChainAnalyzer           rootCause,      RootCauseChainReport             rootCauseReport,
        TraceDumpCorrelationReport       correlationReport)
    {
        _cpu        = cpu;        _cpuReport        = cpuReport;
        _alloc      = alloc;      _allocReport      = allocReport;
        _gc         = gc;         _gcReport         = gcReport;
        _contention = contention; _contentionReport = contentionReport;
        _exceptions = exceptions; _exceptionsReport = exceptionsReport;
        _starvation = starvation; _starvationReport = starvationReport;
        _jit        = jit;        _jitReport        = jitReport;
        _http       = http;       _httpReport       = httpReport;
        _async      = async_;     _asyncReport      = asyncReport;
        _sql        = sql;        _sqlReport        = sqlReport;
        _json       = json;       _jsonReport       = jsonReport;
        _cswitch    = cswitch;    _cswitchReport    = cswitchReport;
        _finalizer    = finalizer;    _finalizerReport    = finalizerReport;
        _connPool     = connPool;     _connPoolReport     = connPoolReport;
        _allocBurst   = allocBurst;   _allocBurstReport   = allocBurstReport;
        _deadlock     = deadlock;     _deadlockReport     = deadlockReport;
        _loh          = loh;          _lohReport          = lohReport;
        _retryStorm   = retryStorm;   _retryStormReport   = retryStormReport;
        _processLife  = processLife;  _processLifeReport  = processLifeReport;
        _taskSched    = taskSched;    _taskSchedReport    = taskSchedReport;
        _fileIo       = fileIo;       _fileIoReport       = fileIoReport;
        _socket       = socket;       _socketReport       = socketReport;
        _dns          = dns;          _dnsReport          = dnsReport;
        _kestrel      = kestrel;      _kestrelReport      = kestrelReport;
        _handleLeak   = handleLeak;   _handleLeakReport   = handleLeakReport;
        _aspnet       = aspnet;       _aspnetReport       = aspnetReport;
        _otel         = otel;         _otelReport         = otelReport;
        _anomaly      = anomaly;      _anomalyReport      = anomalyReport;
        _rootCause    = rootCause;    _rootCauseReport    = rootCauseReport;
        _correlationReport = correlationReport;
    }

    public string Name               => "trace-dump-analyze";
    public string Description        => "Combined trace + dump analysis — runs all 29 trace sub-analyzers, a lightweight dump walk, and cross-source correlation.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective trace-dump-analyze <trace-file> <dump-file> [options]

        Opens a trace file AND a memory dump captured during the same incident, runs
        all trace sub-analyzers, performs a lightweight dump heap walk, then cross-
        correlates findings from both sources into a single combined report.

        This produces the highest-confidence diagnoses available — patterns that
        neither the trace nor the dump can detect alone.

        How to capture both files:
          # 1. Start trace collection
          dotnet-trace collect --profile cpu-sampling -o app.nettrace
          # 2. While trace is running, take a dump
          dotnet-dump collect --process-id <pid> -o app.dmp
          # 3. Stop the trace
          dotnet-trace stop
          # 4. Analyze both together
          DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html

        Alternatively, use --trace / --dump to specify files by name flag:
          DumpDetective trace-dump-analyze --trace perf.etl --dump crash.dmp --output r.html

        Cross-source rules (fire only when BOTH sources agree):
          • Allocation type convergence — top allocator in trace + dominant type in dump
          • Async starvation confirmed — starvation signal + stuck async state machines
          • Exception accumulation — exception storm + live exception objects on heap
          • GC pause × LOH fragmentation — elevated pauses + fragmented large object heap
          • Thread contention × blocked threads — contention in trace + blocked count in dump
          • Slow SQL × connection count — slow queries + elevated live DB connections
          • Pinned handles × GC pause — pinning pressure visible from both sides
          • Finalizer queue backlog — high allocation rate + deep finalizer queue
          • HTTP latency × async backlog — slow HTTP requests + stuck async state machines
          • CPU saturation × thread pool idle — near-full CPU + minimal idle workers

        Options:
          -n, --top <N>            Top N items per trace section (default: 20)
          --process <name>         Filter trace to a specific process name
          --show-system            Include system/kernel frames in CPU tree (default: hidden)
          --slow-ms <ms>           HTTP/SQL slow-request threshold in ms (default: 1000)
          --trace <file>           Explicit trace file path (alternative to positional)
          --dump <file>            Explicit dump file path (alternative to positional)
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective trace-dump-analyze app.nettrace app.dmp
          DumpDetective trace-dump-analyze perf.etl crash.dmp --process w3wp --output incident.html
          DumpDetective trace-dump-analyze app.nettrace app.dmp --slow-ms 500 --top 30
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 100);
        double  slowMs        = a.GetInt("slow-ms", 1000);
        string? processFilter = a.GetOption("process");
        bool    filterSystem  = !a.HasFlag("show-system");

        // ── Resolve trace + dump paths ────────────────────────────────────────
        string? tracePath = a.GetOption("trace")
            ?? a.Positionals.FirstOrDefault(CliArgs.IsTraceFile);

        string? dumpPath = a.GetOption("dump")
            ?? a.Positionals.FirstOrDefault(static p =>
                p.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase));

        if (tracePath is null || dumpPath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Both a trace file and a dump file are required.");
            AnsiConsole.MarkupLine("[dim]Example: DumpDetective trace-dump-analyze app.nettrace app.dmp[/]");
            AnsiConsole.MarkupLine(Markup.Escape(Help));
            return 1;
        }

        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Trace file not found: {Markup.Escape(tracePath)}");
            return 1;
        }
        if (!File.Exists(dumpPath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Dump file not found: {Markup.Escape(dumpPath)}");
            return 1;
        }
        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Expected a trace file (.nettrace or .etl): {Markup.Escape(Path.GetFileName(tracePath))}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Trace:[/] {Markup.Escape(Path.GetFileName(tracePath))}");
        AnsiConsole.MarkupLine($"[bold]Dump:[/]  {Markup.Escape(Path.GetFileName(dumpPath))}");

        TraceLog? trace = null;
        try
        {
            // ── Phase 1: Open trace ───────────────────────────────────────────
            CommandBase.RunStatus("Opening trace file...", _ =>
                trace = TraceLog.OpenOrConvert(tracePath,
                    new TraceLogOptions { ConversionLog = TextWriter.Null }));

            string traceFileName = Path.GetFileName(tracePath);
            string dumpFileName  = Path.GetFileName(dumpPath);

            var outputPaths = a.EffectiveOutputPaths.Count > 0
                ? a.EffectiveOutputPaths
                : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath, ".html")];
            using var sink = SinkFactory.CreateMulti(outputPaths);

            sink.Header("Trace + Dump Combined Analysis",
                $"Trace: {traceFileName}  |  Dump: {dumpFileName}" +
                (processFilter is not null ? $"  |  Process: {processFilter}" : ""),
                navLevel: 1);

            // ── Phase 2: Trace sub-analyzers ──────────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 1 — Trace analysis[/]");

            CmdData.CpuTraceData?                          cpuData        = null;
            CmdData.AllocTraceData?                        allocData      = null;
            CmdData.GcTraceData?                           gcData         = null;
            CmdData.ContentionTraceData?                   contentionData = null;
            CmdData.ExceptionsTraceData?                   exceptionsData = null;
            CmdData.ThreadPoolStarvationData?              starvationData = null;
            CmdData.JitTraceData?                          jitData        = null;
            CmdData.HttpTraceData?                         httpData       = null;
            CmdData.AsyncTraceData?                        asyncData      = null;
            CmdData.SqlTraceData?                          sqlData        = null;
            CmdData.JsonSerializationTraceData?            jsonData       = null;
            CmdData.ContextSwitchTraceData?                cswitchData    = null;
            CmdData.FinalizerTraceData?                    finalizerData   = null;
            CmdData.ConnectionPoolTraceData?               connPoolData    = null;
            CmdData.AllocationBurstData?                   allocBurstData  = null;
            CmdData.DeadlockPatternData?                   deadlockData    = null;
            CmdData.LohTraceData?                          lohData         = null;
            CmdData.RetryStormData?                        retryStormData  = null;
            CmdData.ProcessLifecycleData?                  processLifeData = null;
            CmdData.TaskSchedulerTraceData?                taskSchedData   = null;
            CmdData.FileIoTraceData?                       fileIoData      = null;
            CmdData.SocketTraceData?                       socketData      = null;
            CmdData.DnsTraceData?                          dnsData         = null;
            CmdData.KestrelTraceData?                      kestrelData     = null;
            CmdData.HandleLeakTraceData?                   handleLeakData  = null;
            CmdData.AspNetCorePipelineData?                aspnetData      = null;
            CmdData.OpenTelemetryTraceData?                otelData        = null;
            CmdData.AnomalyDetectionData?                  anomalyData     = null;
            CmdData.RootCauseChainData?                    rootCauseData   = null;
            var captured = new Dictionary<string, ReportDoc>(StringComparer.OrdinalIgnoreCase);

            RunAnalyzer("cpu-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("CPU Trace", traceFileName, navLevel: 3, commandName: "cpu-trace");
                cpuData = _cpu.Analyze(trace!, traceFileName, top, processFilter, filterSystem);
                _cpuReport.Render(cpuData, cap, top);
                captured["cpu-trace"] = cap.GetDoc();
            }, () => cpuData?.TraceInfo);
            RunAnalyzer("alloc-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Allocation Trace", traceFileName, navLevel: 3, commandName: "alloc-trace");
                allocData = _alloc.Analyze(trace!, traceFileName, top, processFilter);
                _allocReport.Render(allocData, cap, top);
                captured["alloc-trace"] = cap.GetDoc();
            }, () => allocData?.TraceInfo);
            RunAnalyzer("gc-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("GC Trace", traceFileName, navLevel: 3, commandName: "gc-trace");
                gcData = _gc.Analyze(trace!, traceFileName, top, processFilter);
                _gcReport.Render(gcData, cap, top);
                captured["gc-trace"] = cap.GetDoc();
            }, () => gcData?.TraceInfo);
            RunAnalyzer("contention-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Contention Trace", traceFileName, navLevel: 3, commandName: "contention-trace");
                contentionData = _contention.Analyze(trace!, traceFileName, top, processFilter);
                _contentionReport.Render(contentionData, cap, top);
                captured["contention-trace"] = cap.GetDoc();
            }, () => contentionData?.TraceInfo);
            RunAnalyzer("exceptions-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Exceptions Trace", traceFileName, navLevel: 3, commandName: "exceptions-trace");
                exceptionsData = _exceptions.Analyze(trace!, traceFileName, top, processFilter);
                _exceptionsReport.Render(exceptionsData, cap, top);
                captured["exceptions-trace"] = cap.GetDoc();
            }, () => exceptionsData?.TraceInfo);
            RunAnalyzer("thread-pool-starvation", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Thread Pool Starvation", traceFileName, navLevel: 3, commandName: "thread-pool-starvation");
                starvationData = _starvation.Analyze(trace!, traceFileName, top);
                _starvationReport.Render(starvationData, cap, top);
                captured["thread-pool-starvation"] = cap.GetDoc();
            }, () => starvationData?.TraceInfo);
            RunAnalyzer("jit-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("JIT Trace", traceFileName, navLevel: 3, commandName: "jit-trace");
                jitData = _jit.Analyze(trace!, traceFileName, top, processFilter);
                _jitReport.Render(jitData, cap, top);
                captured["jit-trace"] = cap.GetDoc();
            }, () => jitData?.TraceInfo);
            RunAnalyzer("http-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("HTTP Trace", traceFileName, navLevel: 3, commandName: "http-trace");
                httpData = _http.Analyze(trace!, traceFileName, top, processFilter, slowMs);
                _httpReport.Render(httpData, cap, top);
                captured["http-trace"] = cap.GetDoc();
            }, () => httpData?.TraceInfo);
            RunAnalyzer("async-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Async / Task Trace", traceFileName, navLevel: 3, commandName: "async-trace");
                asyncData = _async.Analyze(trace!, traceFileName, top, processFilter);
                _asyncReport.Render(asyncData, cap, top);
                captured["async-trace"] = cap.GetDoc();
            }, () => asyncData?.TraceInfo);
            RunAnalyzer("sql-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("SQL / EF Trace", traceFileName, navLevel: 3, commandName: "sql-trace");
                sqlData = _sql.Analyze(trace!, traceFileName, top, processFilter, slowMs);
                _sqlReport.Render(sqlData, cap, top);
                captured["sql-trace"] = cap.GetDoc();
            }, () => sqlData?.TraceInfo);
            RunAnalyzer("json-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("JSON Serialization", traceFileName, navLevel: 3, commandName: "json-trace");
                jsonData = _json.Analyze(trace!, traceFileName, top, processFilter);
                _jsonReport.Render(jsonData, cap, top);
                captured["json-trace"] = cap.GetDoc();
            }, () => jsonData?.TraceInfo);
            RunAnalyzer("context-switch-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Context Switch", traceFileName, navLevel: 3, commandName: "context-switch-trace");
                cswitchData = _cswitch.Analyze(trace!, traceFileName, top, processFilter);
                _cswitchReport.Render(cswitchData, cap, top);
                captured["context-switch-trace"] = cap.GetDoc();
            }, () => cswitchData?.TraceInfo);
            RunAnalyzer("finalizer-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Finalizer Trace", traceFileName, navLevel: 3, commandName: "finalizer-trace");
                finalizerData = _finalizer.Analyze(trace!, traceFileName, top, processFilter);
                _finalizerReport.Render(finalizerData, cap, top);
                captured["finalizer-trace"] = cap.GetDoc();
            }, () => finalizerData?.TraceInfo);
            RunAnalyzer("connection-pool-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Connection Pool Trace", traceFileName, navLevel: 3, commandName: "connection-pool-trace");
                connPoolData = _connPool.Analyze(trace!, traceFileName, top, processFilter);
                _connPoolReport.Render(connPoolData, cap, top);
                captured["connection-pool-trace"] = cap.GetDoc();
            }, () => connPoolData?.TraceInfo);
            RunAnalyzer("alloc-burst-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Allocation Bursts", traceFileName, navLevel: 3, commandName: "alloc-burst-trace");
                allocBurstData = _allocBurst.Analyze(trace!, traceFileName, top, processFilter);
                _allocBurstReport.Render(allocBurstData, cap, top);
                captured["alloc-burst-trace"] = cap.GetDoc();
            }, () => allocBurstData?.TraceInfo);
            RunAnalyzer("deadlock-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Deadlock Detection", traceFileName, navLevel: 3, commandName: "deadlock-trace");
                deadlockData = _deadlock.Analyze(trace!, traceFileName, top, processFilter);
                _deadlockReport.Render(deadlockData, cap, top);
                captured["deadlock-trace"] = cap.GetDoc();
            }, () => deadlockData?.TraceInfo);
            RunAnalyzer("loh-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("LOH Allocation Trace", traceFileName, navLevel: 3, commandName: "loh-trace");
                lohData = _loh.Analyze(trace!, traceFileName, top, processFilter);
                _lohReport.Render(lohData, cap, top);
                captured["loh-trace"] = cap.GetDoc();
            }, () => lohData?.TraceInfo);
            RunAnalyzer("retry-storm-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Retry Storm", traceFileName, navLevel: 3, commandName: "retry-storm-trace");
                retryStormData = _retryStorm.Analyze(trace!, traceFileName, top, processFilter);
                _retryStormReport.Render(retryStormData, cap, top);
                captured["retry-storm-trace"] = cap.GetDoc();
            }, () => retryStormData?.TraceInfo);
            RunAnalyzer("process-lifecycle-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Process Lifecycle", traceFileName, navLevel: 3, commandName: "process-lifecycle-trace");
                processLifeData = _processLife.Analyze(trace!, traceFileName, top, processFilter);
                _processLifeReport.Render(processLifeData, cap, top);
                captured["process-lifecycle-trace"] = cap.GetDoc();
            }, () => processLifeData?.TraceInfo);
            RunAnalyzer("task-scheduler-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Task Scheduler Trace", traceFileName, navLevel: 3, commandName: "task-scheduler-trace");
                taskSchedData = _taskSched.Analyze(trace!, traceFileName, top, processFilter);
                _taskSchedReport.Render(taskSchedData, cap, top);
                captured["task-scheduler-trace"] = cap.GetDoc();
            }, () => taskSchedData?.TraceInfo);
            RunAnalyzer("file-io-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("File I/O Trace", traceFileName, navLevel: 3, commandName: "file-io-trace");
                fileIoData = _fileIo.Analyze(trace!, traceFileName, top, processFilter);
                _fileIoReport.Render(fileIoData, cap, top);
                captured["file-io-trace"] = cap.GetDoc();
            }, () => fileIoData?.TraceInfo);
            RunAnalyzer("socket-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Socket Trace", traceFileName, navLevel: 3, commandName: "socket-trace");
                socketData = _socket.Analyze(trace!, traceFileName, top, processFilter);
                _socketReport.Render(socketData, cap, top);
                captured["socket-trace"] = cap.GetDoc();
            }, () => socketData?.TraceInfo);
            RunAnalyzer("dns-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("DNS Trace", traceFileName, navLevel: 3, commandName: "dns-trace");
                dnsData = _dns.Analyze(trace!, traceFileName, top, processFilter);
                _dnsReport.Render(dnsData, cap, top);
                captured["dns-trace"] = cap.GetDoc();
            }, () => dnsData?.TraceInfo);
            RunAnalyzer("kestrel-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Kestrel Trace", traceFileName, navLevel: 3, commandName: "kestrel-trace");
                kestrelData = _kestrel.Analyze(trace!, traceFileName, top, processFilter);
                _kestrelReport.Render(kestrelData, cap, top);
                captured["kestrel-trace"] = cap.GetDoc();
            }, () => kestrelData?.TraceInfo);
            RunAnalyzer("handle-leak-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Handle Leak Trace", traceFileName, navLevel: 3, commandName: "handle-leak-trace");
                handleLeakData = _handleLeak.Analyze(trace!, traceFileName, top, processFilter);
                _handleLeakReport.Render(handleLeakData, cap, top);
                captured["handle-leak-trace"] = cap.GetDoc();
            }, () => handleLeakData?.TraceInfo);
            RunAnalyzer("aspnetcore-pipeline-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("ASP.NET Core Pipeline", traceFileName, navLevel: 3, commandName: "aspnetcore-pipeline-trace");
                aspnetData = _aspnet.Analyze(trace!, traceFileName, top, processFilter);
                _aspnetReport.Render(aspnetData, cap, top);
                captured["aspnetcore-pipeline-trace"] = cap.GetDoc();
            }, () => aspnetData?.TraceInfo);
            RunAnalyzer("otel-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("OpenTelemetry Trace", traceFileName, navLevel: 3, commandName: "otel-trace");
                otelData = _otel.Analyze(trace!, traceFileName, top, processFilter);
                _otelReport.Render(otelData, cap, top);
                captured["otel-trace"] = cap.GetDoc();
            }, () => otelData?.TraceInfo);
            RunAnalyzer("anomaly-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Anomaly Detection", traceFileName, navLevel: 3, commandName: "anomaly-trace");
                anomalyData = _anomaly.Analyze(traceFileName, processFilter,
                    cpu: cpuData, gc: gcData, alloc: allocBurstData,
                    contention: contentionData, exceptions: exceptionsData);
                _anomalyReport.Render(anomalyData, cap, top);
                captured["anomaly-trace"] = cap.GetDoc();
            }, () => anomalyData?.TraceInfo);

            // ── Phase 2: Lightweight dump walk ────────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 2 — Dump analysis[/]");
            DumpSnapshot? snap = null;
            IReadOnlyDictionary<string, long>? retainedByType = null;
            var dumpLog = new ProgressLogger();
            dumpLog.Stage("Walking dump heap...", indent: true);
            using (var dumpCtx = DumpContext.Open(dumpPath))
            {
                // Try to load a pre-built BFS index (produced by the 'load' command).
                // When available it enables O(N+E) retained-size estimation for top types
                // without any additional ClrMD I/O.
                BfsIndexCache? bfsCache = null;
                if (BfsIndexCache.IsValid(BfsIndexCache.CachePath(dumpPath), dumpPath))
                {
                    CommandBase.RunStatus("Loading BFS index for retained-size estimation...", update =>
                        bfsCache = dumpCtx.GetOrCreateAnalysis<BfsCacheBox>(() =>
                            new BfsCacheBox(BfsIndexCache.TryLoad(dumpPath, update))).Cache);
                }

                snap = DumpCollector.CollectLightweight(dumpCtx, dumpLog.OnProgress);

                // If the BFS cache is available, compute per-type retained sizes so the
                // correlator can compare retained (not just shallow) bytes against the
                // trace's allocation data.
                if (bfsCache is not null && snap.TopTypes.Count > 0)
                {
                    CommandBase.RunStatus("Computing retained sizes from BFS index...", _ =>
                        retainedByType = ComputeTopTypeRetained(dumpCtx, snap, bfsCache));
                }
            }
            dumpLog.CheckM(
                $"Dump walk complete  |  {snap!.TotalObjectCount:N0} objects  •  " +
                $"{Markup.Escape(DumpHelpers.FormatSize(snap.TotalHeapBytes))} heap  •  " +
                $"score [bold]{snap.HealthScore}/100[/]",
                indent: true);

            // Re-render alloc-trace with dump heap data now that snap is available.
            // This replaces the earlier captured doc (estimated-only) with one that
            // shows actual live heap bytes alongside the sampled trace estimates.
            if (allocData is not null)
            {
                CommandBase.RunStatus("Enriching alloc-trace with dump heap sizes...", _ =>
                {
                    var cap = new CaptureSink();
                    cap.Header("Allocation Trace", traceFileName, navLevel: 3, commandName: "alloc-trace");
                    _allocReport.Render(allocData, cap, top, snap!.TopTypes, snap.TotalHeapBytes);
                    captured["alloc-trace"] = cap.GetDoc();
                });
            }

            // ── Phase 3: Cross-source correlation ─────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 3 — Cross-source correlation[/]");
            IReadOnlyList<CorrelationFinding> crossFindings = [];
            CommandBase.RunStatus("Running TraceDumpCorrelator...", _ =>
            {
                crossFindings = TraceDumpCorrelator.Correlate(
                    snap!,
                    alloc:          allocData,
                    gc:             gcData,
                    contention:     contentionData,
                    exceptions:     exceptionsData,
                    starvation:     starvationData,
                    http:           httpData,
                    async_:         asyncData,
                    sql:            sqlData,
                    cpu:            cpuData,
                    finalizer:      finalizerData,
                    allocBurst:     allocBurstData,
                    loh:            lohData,
                    connPool:       connPoolData,
                    deadlock:       deadlockData,
                    handleLeak:     handleLeakData,
                    retainedByType: retainedByType);
            });
            AnsiConsole.MarkupLine($"  [green]✓[/] {crossFindings.Count} cross-source finding(s)");

            // ── Write report: cross-source section first ──────────────────────
            sink.Header("Cross-Source Findings", "", navLevel: 2);
            _correlationReport.Render(crossFindings, snap!, sink);

            // ── Root Cause Analysis ───────────────────────────────────────────
            RunAnalyzer("root-cause-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Root Cause Analysis", traceFileName, navLevel: 3, commandName: "root-cause-trace");
                rootCauseData = _rootCause.Analyze(traceFileName,
                    correlations:  crossFindings,
                    cpu:           cpuData,
                    alloc:         allocData,
                    gc:            gcData,
                    contention:    contentionData,
                    exceptions:    exceptionsData,
                    starvation:    starvationData,
                    jit:           jitData,
                    http:          httpData,
                    async_:        asyncData,
                    sql:           sqlData,
                    finalizer:     finalizerData,
                    connPool:      connPoolData,
                    allocBurst:    allocBurstData,
                    deadlock:      deadlockData,
                    loh:           lohData,
                    retryStorm:    retryStormData,
                    taskScheduler: taskSchedData,
                    fileIo:        fileIoData,
                    socket:        socketData,
                    dns:           dnsData,
                    anomaly:       anomalyData);
                _rootCauseReport.Render(rootCauseData, cap, top);
                captured["root-cause-trace"] = cap.GetDoc();
            }, () => rootCauseData?.TraceInfo);

            // Trace sub-analyzer groups at navLevel 2 so individual reports (captured at
            // navLevel 3) nest correctly inside them as accordion children in the nav.
            (string Heading, string[] Names)[] groups =
            [
                ("CPU & Allocation",         ["cpu-trace", "alloc-trace", "alloc-burst-trace"]),
                ("GC & Memory",              ["gc-trace", "finalizer-trace", "loh-trace"]),
                ("Exceptions & Locks",       ["exceptions-trace", "contention-trace", "deadlock-trace", "retry-storm-trace"]),
                ("Threads & Concurrency",    ["thread-pool-starvation", "async-trace", "context-switch-trace", "task-scheduler-trace"]),
                ("JIT & HTTP",               ["jit-trace", "http-trace", "kestrel-trace", "aspnetcore-pipeline-trace"]),
                ("SQL & Network",            ["sql-trace", "json-trace", "connection-pool-trace", "socket-trace", "dns-trace"]),
                ("Infrastructure",           ["process-lifecycle-trace", "file-io-trace", "handle-leak-trace"]),
                ("Observability",            ["otel-trace"]),
                ("Intelligence",             ["anomaly-trace", "root-cause-trace"]),
            ];

            foreach (var (heading, names) in groups)
            {
                int available = names.Count(n => captured.ContainsKey(n));
                if (available == 0) continue;
                sink.Header($"{heading} ({available})", traceFileName, navLevel: 2);
                foreach (var name in names)
                {
                    if (captured.TryGetValue(name, out var doc))
                        ReportDocReplay.Replay(doc, sink);
                }
            }

            foreach (var p in outputPaths.Where(p =>
                !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(p)}");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Ensure the dump is a valid .NET managed dump.[/]");
            return 1;
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

    private static void RunAnalyzer(string name, Action run, Func<string?>? info = null)
    {
        try
        {
            CommandBase.RunStatus($"Running {name}...", _ => run());
            string stats = SummaryStats(info?.Invoke());
            if (stats.Length > 0)
                AnsiConsole.MarkupLine($"  [green]✓[/] {name}  [dim]{Markup.Escape(stats)}[/]");
            else
                AnsiConsole.MarkupLine($"  [green]✓[/] {name}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] {name} failed: {Markup.Escape(ex.Message)}");
        }
    }

    /// <summary>
    /// Strips the filename/process prefix from a TraceInfo string — takes the text
    /// after the last "  |  " separator so only the stats portion is shown.
    /// </summary>
    private static string SummaryStats(string? info)
    {
        if (info is null) return "";
        int idx = info.LastIndexOf("  |  ", StringComparison.Ordinal);
        return idx >= 0 ? info[(idx + 5)..] : info;
    }

    /// <summary>
    /// Enumerates the managed heap once to collect up to <c>maxSamplesPerType</c>
    /// object addresses for each type listed in <paramref name="snap"/>.TopTypes, then
    /// uses the pre-built <paramref name="bfsCache"/> to compute total retained bytes per
    /// type and extrapolates from the sample to the full instance count.
    ///
    /// A fresh <see cref="HashSet{T}"/> is used per type so that shared subgraphs are
    /// counted fully for each type independently (rather than being attributed to
    /// whichever type first claimed them). A BFS node cap prevents runaway walks on
    /// extremely large object graphs.
    /// </summary>
    private static IReadOnlyDictionary<string, long> ComputeTopTypeRetained(
        DumpContext ctx, DumpSnapshot snap, BfsIndexCache bfsCache)
    {
        const int  maxSamplesPerType = 50;
        const long bfsNodeCap        = 500_000; // caps BFS at ~500k nodes per instance

        // One heap pass: collect up to maxSamplesPerType addresses per top type.
        var typeNames   = new HashSet<string>(snap.TopTypes.Select(t => t.Name), StringComparer.Ordinal);
        var addrsByType = new Dictionary<string, List<ulong>>(snap.TopTypes.Count, StringComparer.Ordinal);

        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
            string? name = obj.Type.Name;
            if (name is null || !typeNames.Contains(name)) continue;
            if (!addrsByType.TryGetValue(name, out var list))
                addrsByType[name] = list = new List<ulong>(maxSamplesPerType);
            if (list.Count < maxSamplesPerType)
                list.Add(obj.Address);
        }

        // For each top type compute BFS retained for the sampled instances, then
        // extrapolate to the full instance count.
        var result = new Dictionary<string, long>(addrsByType.Count, StringComparer.Ordinal);
        foreach (var ts in snap.TopTypes)
        {
            if (!addrsByType.TryGetValue(ts.Name, out var addrs) || addrs.Count == 0) continue;

            // Fresh visited set per type: each type gets an independent retained-size
            // estimate (shared subgraphs may be counted in multiple types, which is the
            // correct behaviour for a per-type dominance check).
            var  visited         = new HashSet<int>(capacity: 4096);
            long sampledRetained = 0;
            foreach (var addr in addrs)
            {
                var (sz, _) = bfsCache.ComputeRetained(addr, visited, bfsNodeCap);
                sampledRetained += sz;
            }

            // Extrapolate: scale up from sampled count to full instance count.
            long total = addrs.Count < (int)ts.Count
                ? sampledRetained * ts.Count / addrs.Count
                : sampledRetained;
            result[ts.Name] = total;
        }

        return result;
    }

    // trace-dump-analyze requires both a trace file and a dump path —
    // it cannot be driven from DumpContext alone.
    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "trace-dump-analyze requires both a trace file and a dump file.",
            "Use the Run() entry point: DumpDetective trace-dump-analyze <trace-file> <dump-file>",
            "Example: DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html");
}
