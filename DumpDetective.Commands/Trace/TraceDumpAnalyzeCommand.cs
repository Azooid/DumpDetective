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
    private readonly AllocTraceAnalyzer            _alloc;
    private readonly GcTraceAnalyzer               _gc;
    private readonly ContentionTraceAnalyzer       _contention;
    private readonly ExceptionsTraceAnalyzer       _exceptions;
    private readonly ThreadPoolStarvationAnalyzer  _starvation;
    private readonly JitTraceAnalyzer              _jit;
    private readonly HttpTraceAnalyzer             _http;
    private readonly AsyncTraceAnalyzer            _async;
    private readonly SqlTraceAnalyzer              _sql;

    private readonly CpuTraceReport              _cpuReport;
    private readonly AllocTraceReport            _allocReport;
    private readonly GcTraceReport               _gcReport;
    private readonly ContentionTraceReport       _contentionReport;
    private readonly ExceptionsTraceReport       _exceptionsReport;
    private readonly ThreadPoolStarvationReport  _starvationReport;
    private readonly JitTraceReport              _jitReport;
    private readonly HttpTraceReport             _httpReport;
    private readonly AsyncTraceReport            _asyncReport;
    private readonly SqlTraceReport              _sqlReport;
    private readonly TraceDumpCorrelationReport  _correlationReport;

    public TraceDumpAnalyzeCommand(
        CpuTraceAnalyzer             cpu,             CpuTraceReport              cpuReport,
        AllocTraceAnalyzer           alloc,           AllocTraceReport            allocReport,
        GcTraceAnalyzer              gc,              GcTraceReport               gcReport,
        ContentionTraceAnalyzer      contention,      ContentionTraceReport       contentionReport,
        ExceptionsTraceAnalyzer      exceptions,      ExceptionsTraceReport       exceptionsReport,
        ThreadPoolStarvationAnalyzer starvation,      ThreadPoolStarvationReport  starvationReport,
        JitTraceAnalyzer             jit,             JitTraceReport              jitReport,
        HttpTraceAnalyzer            http,            HttpTraceReport             httpReport,
        AsyncTraceAnalyzer           async_,          AsyncTraceReport            asyncReport,
        SqlTraceAnalyzer             sql,             SqlTraceReport              sqlReport,
        TraceDumpCorrelationReport   correlationReport)
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
        _correlationReport = correlationReport;
    }

    public string Name               => "trace-dump-analyze";
    public string Description        => "Combined trace + dump analysis — runs all trace sub-analyzers and a lightweight dump walk, then cross-correlates findings from both sources.";
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
        int     top           = a.GetInt("top", 20);
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

            CmdData.CpuTraceData?             cpuData        = null;
            CmdData.AllocTraceData?           allocData      = null;
            CmdData.GcTraceData?              gcData         = null;
            CmdData.ContentionTraceData?      contentionData = null;
            CmdData.ExceptionsTraceData?      exceptionsData = null;
            CmdData.ThreadPoolStarvationData? starvationData = null;
            CmdData.JitTraceData?             jitData        = null;
            CmdData.HttpTraceData?            httpData       = null;
            CmdData.AsyncTraceData?           asyncData      = null;
            CmdData.SqlTraceData?             sqlData        = null;
            var captured = new Dictionary<string, ReportDoc>(StringComparer.OrdinalIgnoreCase);

            RunAnalyzer("cpu-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("CPU Trace", traceFileName, navLevel: 3, commandName: "cpu-trace");
                cpuData = _cpu.Analyze(trace!, traceFileName, top, processFilter, filterSystem);
                _cpuReport.Render(cpuData, cap, top);
                captured["cpu-trace"] = cap.GetDoc();
            });
            RunAnalyzer("alloc-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Allocation Trace", traceFileName, navLevel: 3, commandName: "alloc-trace");
                allocData = _alloc.Analyze(trace!, traceFileName, top, processFilter);
                _allocReport.Render(allocData, cap, top);
                captured["alloc-trace"] = cap.GetDoc();
            });
            RunAnalyzer("gc-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("GC Trace", traceFileName, navLevel: 3, commandName: "gc-trace");
                gcData = _gc.Analyze(trace!, traceFileName, top, processFilter);
                _gcReport.Render(gcData, cap, top);
                captured["gc-trace"] = cap.GetDoc();
            });
            RunAnalyzer("contention-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Contention Trace", traceFileName, navLevel: 3, commandName: "contention-trace");
                contentionData = _contention.Analyze(trace!, traceFileName, top, processFilter);
                _contentionReport.Render(contentionData, cap, top);
                captured["contention-trace"] = cap.GetDoc();
            });
            RunAnalyzer("exceptions-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Exceptions Trace", traceFileName, navLevel: 3, commandName: "exceptions-trace");
                exceptionsData = _exceptions.Analyze(trace!, traceFileName, top, processFilter);
                _exceptionsReport.Render(exceptionsData, cap, top);
                captured["exceptions-trace"] = cap.GetDoc();
            });
            RunAnalyzer("thread-pool-starvation", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Thread Pool Starvation", traceFileName, navLevel: 3, commandName: "thread-pool-starvation");
                starvationData = _starvation.Analyze(trace!, traceFileName, top);
                _starvationReport.Render(starvationData, cap, top);
                captured["thread-pool-starvation"] = cap.GetDoc();
            });
            RunAnalyzer("jit-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("JIT Trace", traceFileName, navLevel: 3, commandName: "jit-trace");
                jitData = _jit.Analyze(trace!, traceFileName, top, processFilter);
                _jitReport.Render(jitData, cap, top);
                captured["jit-trace"] = cap.GetDoc();
            });
            RunAnalyzer("http-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("HTTP Trace", traceFileName, navLevel: 3, commandName: "http-trace");
                httpData = _http.Analyze(trace!, traceFileName, top, processFilter, slowMs);
                _httpReport.Render(httpData, cap, top);
                captured["http-trace"] = cap.GetDoc();
            });
            RunAnalyzer("async-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("Async / Task Trace", traceFileName, navLevel: 3, commandName: "async-trace");
                asyncData = _async.Analyze(trace!, traceFileName, top, processFilter);
                _asyncReport.Render(asyncData, cap, top);
                captured["async-trace"] = cap.GetDoc();
            });
            RunAnalyzer("sql-trace", () =>
            {
                var cap = new CaptureSink();
                cap.Header("SQL / EF Trace", traceFileName, navLevel: 3, commandName: "sql-trace");
                sqlData = _sql.Analyze(trace!, traceFileName, top, processFilter, slowMs);
                _sqlReport.Render(sqlData, cap, top);
                captured["sql-trace"] = cap.GetDoc();
            });

            // ── Phase 2: Lightweight dump walk ────────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 2 — Dump analysis[/]");
            DumpSnapshot? snap = null;
            var dumpLog = new ProgressLogger();
            dumpLog.Stage("Walking dump heap...", indent: true);
            using (var dumpCtx = DumpContext.Open(dumpPath))
            {
                snap = DumpCollector.CollectLightweight(dumpCtx, dumpLog.OnProgress);
            }
            dumpLog.CheckM(
                $"Dump walk complete  |  {snap!.TotalObjectCount:N0} objects  •  " +
                $"{Markup.Escape(DumpHelpers.FormatSize(snap.TotalHeapBytes))} heap  •  " +
                $"score [bold]{snap.HealthScore}/100[/]",
                indent: true);

            // ── Phase 3: Cross-source correlation ─────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 3 — Cross-source correlation[/]");
            IReadOnlyList<CorrelationFinding> crossFindings = [];
            CommandBase.RunStatus("Running TraceDumpCorrelator...", _ =>
            {
                crossFindings = TraceDumpCorrelator.Correlate(
                    snap!,
                    alloc:      allocData,
                    gc:         gcData,
                    contention: contentionData,
                    exceptions: exceptionsData,
                    starvation: starvationData,
                    http:       httpData,
                    async_:     asyncData,
                    sql:        sqlData,
                    cpu:        cpuData);
            });
            AnsiConsole.MarkupLine($"  [green]✓[/] {crossFindings.Count} cross-source finding(s)");

            // ── Write report: cross-source section first ──────────────────────
            sink.Header("Cross-Source Findings", "", navLevel: 2);
            _correlationReport.Render(crossFindings, snap!, sink);

            // Then grouped trace sub-analyzer sections.
            sink.Header("Trace Analysis Detail", traceFileName, navLevel: 2);

            (string Heading, string[] Names)[] groups =
            [
                ("CPU / Allocation",        ["cpu-trace", "alloc-trace"]),
                ("GC / Exceptions / Locks", ["gc-trace", "exceptions-trace", "contention-trace"]),
                ("Threads / Concurrency",   ["thread-pool-starvation", "async-trace"]),
                ("JIT / HTTP / SQL",        ["jit-trace", "http-trace", "sql-trace"]),
            ];

            foreach (var (heading, names) in groups)
            {
                int available = names.Count(n => captured.ContainsKey(n));
                if (available == 0) continue;
                sink.Header($"{heading} ({available})", traceFileName, navLevel: 3);
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

    private static void RunAnalyzer(string name, Action run)
    {
        try
        {
            CommandBase.RunStatus($"Running {name}...", _ => run());
            AnsiConsole.MarkupLine($"  [green]✓[/] {name}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] {name} failed: {Markup.Escape(ex.Message)}");
        }
    }

    // trace-dump-analyze requires both a trace file and a dump path —
    // it cannot be driven from DumpContext alone.
    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "trace-dump-analyze requires both a trace file and a dump file.",
            "Use the Run() entry point: DumpDetective trace-dump-analyze <trace-file> <dump-file>",
            "Example: DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html");
}
