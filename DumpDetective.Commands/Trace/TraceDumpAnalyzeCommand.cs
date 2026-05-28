using DumpDetective.Analysis.Memory;
using DumpDetective.Analysis.Trace;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;

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
    private readonly IReadOnlyList<ITraceSubAnalyzer> _subAnalyzers;
    private readonly IReadOnlyList<ITraceSubAnalyzer>       _pluginSubAnalyzers;
    private readonly TraceDumpCorrelationReport             _correlationReport;
    private readonly IReadOnlyList<ITracePlugin>            _pluginTracePlugins;
    private readonly IReadOnlyList<ITraceDumpCorrelationRule> _pluginCorrelationRules;
    private readonly IReadOnlyDictionary<string, string>?   _pluginTraceNames;

    public TraceDumpAnalyzeCommand(
        IReadOnlyList<ITraceSubAnalyzer> subAnalyzers,
        IReadOnlyList<ITraceSubAnalyzer> pluginSubAnalyzers,
        TraceDumpCorrelationReport correlationReport,
        IReadOnlyList<ITracePlugin>? pluginTracePlugins = null,
        IReadOnlyList<ITraceDumpCorrelationRule>? pluginCorrelationRules = null,
        IReadOnlyDictionary<string, string>? pluginTraceNames = null)
    {
        _subAnalyzers       = subAnalyzers;
        _pluginSubAnalyzers = pluginSubAnalyzers;
        _correlationReport  = correlationReport;
        _pluginTracePlugins = pluginTracePlugins ?? [];
        _pluginCorrelationRules = pluginCorrelationRules ?? [];
        _pluginTraceNames   = pluginTraceNames;
    }

    public string Name               => "trace-dump-analyze";
    public string Description        => "Combined trace + dump analysis — runs all 29 trace sub-analyzers, a lightweight dump walk, and cross-source correlation (plugin-extensible with --with-plugins).";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Orchestrator / Cross-source";
    public CommandKind Kind               => CommandKind.Trace;

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
          --show-unresolved        Include unresolved frames in CPU tree (default: hidden)
          --slow-ms <ms>           HTTP/SQL slow-request threshold in ms (default: 1000)
          --trace <file>           Explicit trace file path (alternative to positional)
          --dump <file>            Explicit dump file path (alternative to positional)
          --with-plugins           Include plugin sub-analyzers and plugin correlation rules (default: excluded)
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
        bool    filterUnresolved = !a.HasFlag("show-unresolved");
        bool    withPlugins   = a.HasFlag("with-plugins");

        var effectiveSubs = (withPlugins && _pluginSubAnalyzers.Count > 0)
            ? (IReadOnlyList<ITraceSubAnalyzer>)[.._subAnalyzers, .._pluginSubAnalyzers]
            : _subAnalyzers;

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

        // Resolve companion ETL → base so TraceLog.OpenOrConvert auto-merges all companions.
        string resolvedTrace = EtlPathHelper.ResolveToBase(tracePath);
        if (!string.Equals(resolvedTrace, tracePath, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[dim]↪ Companion ETL detected. Using base file: {Markup.Escape(Path.GetFileName(resolvedTrace))}[/]");
            tracePath = resolvedTrace;
        }
        var traceCompanions = EtlPathHelper.FindCompanionNames(tracePath!);
        AnsiConsole.MarkupLine($"[bold]Trace:[/] {Markup.Escape(tracePath)}");
        if (traceCompanions.Count > 0)
            AnsiConsole.MarkupLine($"[dim]  + {traceCompanions.Count} companion file(s) will be auto-merged: {Markup.Escape(string.Join(", ", traceCompanions))}[/]");
        AnsiConsole.MarkupLine($"[bold]Dump:[/]  {Markup.Escape(dumpPath)}");

        TraceLog? trace = null;
        try
        {
            // ── Phase 1: Open trace ───────────────────────────────────────────
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath, s => update($"{Name}  {s}")));

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

            // ── Phase 1: Trace sub-analyzers ──────────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 1 — Trace analysis[/]");
            var runParams = new TraceRunParams(top, processFilter, filterSystem, filterUnresolved, slowMs);
            var results   = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var captured  = new Dictionary<string, ReportDoc>(StringComparer.OrdinalIgnoreCase);

            // Build one consumer per sub-analyzer, run ONE event loop, then complete.
            var consumerPairs = new List<(ITraceSubAnalyzer Sub, ITraceEventConsumer Consumer)>();
            foreach (var sub in effectiveSubs)
            {
                if (sub.HasCorrelationPhase || !sub.SupportsConsumer) continue;
                var consumer = sub.CreateConsumer(runParams, traceFileName);
                if (consumer is not null)
                    consumerPairs.Add((sub, consumer));
            }

            if (consumerPairs.Count > 0)
            {
                var consumers = consumerPairs.Select(x => x.Consumer).ToList();
                DispatchStats dispatchStats = default;
                CommandBase.RunStatus("Scanning trace events...", update =>
                    dispatchStats = TraceEventDispatcher.Dispatch(trace!, consumers, update));
                results["__dispatch_stats__"] = dispatchStats;
            }

            foreach (var (sub, consumer) in consumerPairs)
            {
                string? traceInfo = null;
                RunAnalyzer(sub.Key,
                    _ => traceInfo = sub.CompleteFromConsumer(consumer, traceFileName, runParams, captured, results),
                    () => traceInfo);
            }

            // Non-consumer analyzers (reads from results dict)
            foreach (var sub in effectiveSubs)
            {
                if (sub.HasCorrelationPhase || sub.SupportsConsumer) continue;
                string? traceInfo = null;
                RunAnalyzer(sub.Key,
                    update => traceInfo = sub.Run(trace!, traceFileName, runParams, captured, results, s => update($"{sub.Key}  {s}")),
                    () => traceInfo);
            }

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

            // Notify sub-analyzers that dump data is available (AllocTraceSubAnalyzer
            // re-renders its report with live heap sizes).
            CommandBase.RunStatus("Enriching reports with dump heap sizes...", _ =>
            {
                foreach (var sub in effectiveSubs)
                    sub.OnDumpAvailable(traceFileName, snap!, captured, results, top);
            });

            // ── Phase 3: Cross-source correlation ─────────────────────────────
            AnsiConsole.MarkupLine("\n[bold]Phase 3 — Cross-source correlation[/]");
            IReadOnlyList<CorrelationFinding> crossFindings = [];
            CommandBase.RunStatus("Running TraceDumpCorrelator...", _ =>
            {
                crossFindings = TraceDumpCorrelator.Correlate(
                    snap!,
                    alloc:          results.GetValueOrDefault("alloc-trace")            as AllocTraceData,
                    gc:             results.GetValueOrDefault("gc-trace")               as GcTraceData,
                    contention:     results.GetValueOrDefault("contention-trace")       as ContentionTraceData,
                    exceptions:     results.GetValueOrDefault("exceptions-trace")       as ExceptionsTraceData,
                    starvation:     results.GetValueOrDefault("thread-pool-starvation") as ThreadPoolStarvationData,
                    http:           results.GetValueOrDefault("http-trace")             as HttpTraceData,
                    async_:         results.GetValueOrDefault("async-trace")            as AsyncTraceData,
                    sql:            results.GetValueOrDefault("sql-trace")              as SqlTraceData,
                    cpu:            results.GetValueOrDefault("cpu-trace")              as CpuTraceData,
                    finalizer:      results.GetValueOrDefault("finalizer-trace")        as FinalizerTraceData,
                    allocBurst:     results.GetValueOrDefault("alloc-burst-trace")      as AllocationBurstData,
                    loh:            results.GetValueOrDefault("loh-trace")              as LohTraceData,
                    connPool:       results.GetValueOrDefault("connection-pool-trace")  as ConnectionPoolTraceData,
                    deadlock:       results.GetValueOrDefault("deadlock-trace")         as DeadlockPatternData,
                    handleLeak:     results.GetValueOrDefault("handle-leak-trace")      as HandleLeakTraceData,
                        retainedByType: retainedByType,
                        pluginRules: withPlugins ? _pluginCorrelationRules : []);
            });
            AnsiConsole.MarkupLine($"  [green]✓[/] {crossFindings.Count} cross-source finding(s)");

            // ── Combined Summary Dashboard ────────────────────────────────────
            RenderCombinedSummary(
                sink, traceFileName, dumpFileName, snap!,
                results.GetValueOrDefault("cpu-trace")              as CpuTraceData,
                results.GetValueOrDefault("alloc-trace")            as AllocTraceData,
                results.GetValueOrDefault("gc-trace")               as GcTraceData,
                results.GetValueOrDefault("contention-trace")       as ContentionTraceData,
                results.GetValueOrDefault("exceptions-trace")       as ExceptionsTraceData,
                results.GetValueOrDefault("thread-pool-starvation") as ThreadPoolStarvationData,
                results.GetValueOrDefault("http-trace")             as HttpTraceData,
                results.GetValueOrDefault("async-trace")            as AsyncTraceData,
                results.GetValueOrDefault("sql-trace")              as SqlTraceData,
                crossFindings, processFilter);

            // ── Write report: cross-source section first ──────────────────────
            sink.Header("Cross-Source Findings", "", navLevel: 2);
            _correlationReport.Render(crossFindings, snap!, sink);

            // Run correlation-phase sub-analyzers (RootCauseSubAnalyzer).
            foreach (var sub in effectiveSubs)
            {
                if (!sub.HasCorrelationPhase) continue;
                string? traceInfo = null;
                RunAnalyzer(sub.Key,
                    update => traceInfo = sub.OnCorrelationAvailable(traceFileName, crossFindings, captured, results, top),
                    () => traceInfo);
            }

            // ── Write grouped trace sections ──────────────────────────────────
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

            // ── Plugin replay (ITraceSubAnalyzer + ITracePlugin) ────────────
            // ITraceSubAnalyzer plugins ran via effectiveSubs above.
            // ITracePlugin plugins (Core-only interface) run here.
            if (withPlugins)
            {
                foreach (var pl in _pluginTracePlugins)
                {
                    string? traceInfo = null;
                    RunAnalyzer(pl.Key, _ =>
                    {
                        var cap = new CaptureSink();
                        cap.Header(pl.SectionTitle, traceFileName, navLevel: 3, commandName: pl.Key);
                        traceInfo = pl.Analyze(trace!, traceFileName, top, processFilter, cap);
                        var plDoc = cap.GetDoc();
                        if (_pluginTraceNames?.TryGetValue(pl.Key, out var plDisplayName) == true)
                            foreach (var ch in plDoc.Chapters) ch.PluginName ??= plDisplayName;
                        captured[pl.Key] = plDoc;
                    }, () => traceInfo);
                }

                // Stamp PluginName on ITraceSubAnalyzer plugin docs captured during Phase 1.
                if (_pluginTraceNames is not null)
                    foreach (var sub in _pluginSubAnalyzers)
                        if (_pluginTraceNames.TryGetValue(sub.Key, out var subDisplayName) &&
                            captured.TryGetValue(sub.Key, out var subDoc))
                            foreach (var ch in subDoc.Chapters) ch.PluginName ??= subDisplayName;
            }

            var allPluginKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (withPlugins)
            {
                foreach (var p in _pluginSubAnalyzers) allPluginKeys.Add(p.Key);
                foreach (var p in _pluginTracePlugins) allPluginKeys.Add(p.Key);
            }
            var allPluginDocs = allPluginKeys.Count > 0
                ? captured.Where(kv => allPluginKeys.Contains(kv.Key)).ToList()
                : [];
            if (allPluginDocs.Count > 0)
            {
                sink.Header($"Plugins ({allPluginDocs.Count})", traceFileName, navLevel: 2);
                foreach (var (_, doc) in allPluginDocs)
                    ReportDocReplay.Replay(doc, sink);
            }

            // ── Event Type Inventory ──────────────────────────────────────────
            if (results.GetValueOrDefault("__dispatch_stats__") is DispatchStats ds)
                TraceEventTypesSection.Render(sink, ds);

            foreach (var op in outputPaths)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(op)}");

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

    private static void RunAnalyzer(string name, Action<Action<string>> body, Func<string?>? info = null)
    {
        try
        {
            CommandBase.RunStatus($"Running {name}...", body);
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

    // ─────────────────────────────────────────────────────────────────────────
    // Combined Summary Dashboard — emitted as the FIRST chapter of the report
    // so readers get a visual overview before wading into detailed sub-sections.
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderCombinedSummary(
        IRenderSink                       sink,
        string                            traceFileName,
        string                            dumpFileName,
        DumpSnapshot                      snap,
        CpuTraceData?                     cpu,
        AllocTraceData?                   alloc,
        GcTraceData?                      gc,
        ContentionTraceData?              contention,
        ExceptionsTraceData?              exceptions,
        ThreadPoolStarvationData?         starvation,
        HttpTraceData?                    http,
        AsyncTraceData?                   async_,
        SqlTraceData?                     sql,
        IReadOnlyList<CorrelationFinding> crossFindings,
        string?                           processFilter)
    {
        sink.Header("Combined Summary", "", navLevel: 2);

        // ── Section A: Session Identity ──────────────────────────────────────
        sink.Section("Session Overview", "combined-session");

        string traceDuration = "—";
        string avgCpu        = "—";
        string peakCpu       = "—";
        string process       = processFilter ?? "(all processes)";

        if (cpu?.Stats is { } cpuStats)
        {
            traceDuration = cpuStats.TraceDurationMs >= 60_000
                ? $"{cpuStats.TraceDurationMs / 60_000.0:F1} min"
                : $"{cpuStats.TraceDurationMs / 1000.0:F1} s";
            avgCpu  = $"{cpuStats.AvgCpuPct:F1} %";
            peakCpu = $"{cpuStats.MaxCpuPct:F1} %";
            if (!string.IsNullOrWhiteSpace(cpuStats.TopProcessName))
                process = cpuStats.TopProcessName;
        }

        string scoreLabel = snap.HealthScore >= 70 ? "Healthy"
                          : snap.HealthScore >= 40 ? "Degraded"
                          : "Critical";

        int critCount = crossFindings.Count(f => f.Severity == FindingSeverity.Critical);

        var kvItems = new List<(string, string)>
        {
            ("Trace file",          traceFileName),
            ("Dump file",           dumpFileName),
            ("Process",             process),
            ("Trace duration",      traceDuration),
            ("Avg CPU (trace)",     avgCpu),
            ("Peak CPU (trace)",    peakCpu),
            ("Dump CLR",            snap.ClrVersion ?? "—"),
            ("Heap at capture",     DumpHelpers.FormatSize(snap.TotalHeapBytes)),
            ("Total objects",       snap.TotalObjectCount.ToString("N0")),
            ("Threads",             $"{snap.AliveThreadCount} alive, {snap.BlockedThreadCount} blocked"),
            ("Thread-pool workers", snap.TpMaxWorkers > 0 ? $"{snap.TpActiveWorkers}/{snap.TpMaxWorkers} active" : "—"),
            ("Async backlog",       snap.AsyncBacklogTotal > 0 ? snap.AsyncBacklogTotal.ToString("N0") : "—"),
            ("Finalizer queue",     snap.FinalizerQueueDepth > 0 ? snap.FinalizerQueueDepth.ToString("N0") : "—"),
            ("Pinned handles",      snap.PinnedHandleCount > 0 ? snap.PinnedHandleCount.ToString("N0") : "—"),
            ("GC events (trace)",   gc?.TotalGcs > 0 ? gc.TotalGcs.ToString("N0") : "—"),
            ("Max GC pause",        gc?.MaxPauseMs > 0 ? $"{gc.MaxPauseMs:F1} ms" : "—"),
            ("Exceptions thrown",   exceptions?.TotalThrown > 0 ? exceptions.TotalThrown.ToString("N0") : "—"),
            ("Lock contentions",    contention?.TotalContentions > 0 ? contention.TotalContentions.ToString("N0") : "—"),
            ("HTTP requests",       http?.HasData == true && http.TotalRequests > 0 ? http.TotalRequests.ToString("N0") : "—"),
            ("HTTP P99 latency",    http?.HasData == true && http.P99RequestMs > 0 ? $"{http.P99RequestMs:F0} ms" : "—"),
            ("SQL commands",        sql?.HasData == true && sql.TotalCommands > 0 ? sql.TotalCommands.ToString("N0") : "—"),
            ("DB connections",      snap.ConnectionCount > 0 ? snap.ConnectionCount.ToString("N0") : "—"),
            ("Cross-src findings",  crossFindings.Count > 0
                ? $"{crossFindings.Count}  ({critCount} critical)"
                : "0"),
            ("Dump health score",   $"{snap.HealthScore}/100  [{scoreLabel}]"),
        };
        // Strip "—" rows to keep the card tight
        sink.KeyValues(kvItems.Where(kv => kv.Item2 != "—").ToList());

        // ── Section B: Cross-Source Signal Matrix ─────────────────────────────
        sink.Section("Cross-Source Signal Matrix", "combined-matrix");
        sink.Explain(
            what:   "Side-by-side comparison of each area from two angles: the trace shows WHEN things happened; the dump shows WHAT existed on the heap at capture time.",
            why:    "A signal observed independently in both sources carries far higher confidence than any single-source observation.",
            impact: "Rows marked 🔴 are corroborated root causes. Rows marked ⚠ are worth investigating further. Scroll to 'Cross-Source Findings' for ranked detail and remediation guidance.",
            action: "Start with 🔴 rows, then ⚠ rows. Use the 'Recommended Next Steps' table to choose follow-up dump commands.");

        var matrixRows = new List<string[]>();

        // CPU
        if (cpu?.Stats is { AvgCpuPct: > 0 } cs)
        {
            string trSig = cs.AvgCpuPct >= 85
                ? $"🔴 Saturated  ({cs.AvgCpuPct:F1}% avg, {cs.MaxCpuPct:F1}% peak)"
                : cs.AvgCpuPct >= 70
                    ? $"⚠ High  ({cs.AvgCpuPct:F1}% avg, {cs.MaxCpuPct:F1}% peak)"
                    : $"✓ Normal  ({cs.AvgCpuPct:F1}% avg)";
            string dumpSig = snap.TpMaxWorkers > 0
                ? $"{snap.TpActiveWorkers}/{snap.TpMaxWorkers} thread-pool workers active"
                : $"{snap.AliveThreadCount} live threads";
            string combined = cs.AvgCpuPct >= 85
                    && snap.TpMaxWorkers > 0
                    && snap.TpActiveWorkers >= snap.TpMaxWorkers * 0.9
                ? "🔴 CPU + pool saturated"
                : cs.AvgCpuPct >= 70 ? "⚠ CPU high" : "✓ OK";
            matrixRows.Add(["CPU", trSig, dumpSig, combined]);
        }

        // Memory / GC
        if (gc?.TotalGcs > 0 || snap.TotalHeapBytes > 0)
        {
            string trSig = gc?.MaxPauseMs >= 200
                ? $"🔴 Max GC pause {gc.MaxPauseMs:F0} ms  ({gc.TotalGcs:N0} GCs, {gc.TotalPauseMs:F0} ms total)"
                : gc?.MaxPauseMs > 0
                    ? $"⚠ Max pause {gc.MaxPauseMs:F1} ms  ({gc.TotalGcs:N0} GCs)"
                    : gc?.TotalGcs > 0
                        ? $"✓ {gc.TotalGcs:N0} GCs, no long pauses"
                        : "—";
            double gen2Pct = snap.TotalHeapBytes > 0 ? snap.Gen2Bytes * 100.0 / snap.TotalHeapBytes : 0;
            string dumpSig = $"{DumpHelpers.FormatSize(snap.TotalHeapBytes)} heap  •  Gen2={gen2Pct:F0}%  •  frag={snap.FragmentationPct:F0}%";
            string combined = gc?.MaxPauseMs >= 200 && gen2Pct >= 60
                ? "🔴 GC pressure confirmed"
                : gc?.MaxPauseMs >= 200 || gen2Pct >= 70 || snap.FragmentationPct >= 30
                    ? "⚠ GC pressure"
                    : "✓ OK";
            matrixRows.Add(["Memory / GC", trSig, dumpSig, combined]);
        }

        // Allocations
        if (alloc?.EstimatedTotalBytes > 0 || snap.TotalHeapBytes > 0)
        {
            string topAllocType = alloc?.TopTypes?.Count > 0 ? TrimTypeName(alloc.TopTypes[0].TypeName, 36) : "";
            string trSig = alloc?.EstimatedTotalBytes > 0
                ? $"~{DumpHelpers.FormatSize(alloc.EstimatedTotalBytes)} allocated"
                  + (topAllocType.Length > 0 ? $"  •  top: {topAllocType}" : "")
                : "—";
            string topLiveType = snap.TopTypes.Count > 0 ? TrimTypeName(snap.TopTypes[0].Name, 36) : "—";
            string dumpSig = $"Top live: {topLiveType}  ({DumpHelpers.FormatSize(snap.TopTypes.Count > 0 ? snap.TopTypes[0].TotalBytes : 0)})";
            bool converges = alloc?.TopTypes?.Count > 0 && snap.TopTypes.Count > 0
                && snap.TopTypes.Take(5).Any(t => t.Name.Contains(
                    alloc.TopTypes[0].TypeName.Split('.').Last(),
                    StringComparison.OrdinalIgnoreCase));
            string combined = converges ? "⚠ Alloc+live type convergence" : "ℹ No direct type overlap";
            matrixRows.Add(["Allocations", trSig, dumpSig, combined]);
        }

        // Async / Thread Pool
        {
            string trSig = starvation?.StarvationAdjustmentCount >= 5
                ? $"🔴 {starvation.StarvationAdjustmentCount} starvation adj  ({starvation.TotalEvents} TP events)"
                : starvation?.StarvationAdjustmentCount >= 1
                    ? $"⚠ {starvation.StarvationAdjustmentCount} starvation adj"
                    : async_?.HasData == true && async_.SyncBlockingOccurrences > 0
                        ? $"⚠ {async_.SyncBlockingOccurrences} sync-over-async sites"
                        : async_?.HasData == true && async_.TotalTasksScheduled > 0
                            ? $"ℹ {async_.TotalTasksScheduled:N0} tasks scheduled"
                            : "—";
            string dumpSig = snap.AsyncBacklogTotal >= 100
                ? $"🔴 {snap.AsyncBacklogTotal:N0} pending continuations"
                : snap.AsyncBacklogTotal >= 10
                    ? $"⚠ {snap.AsyncBacklogTotal:N0} pending continuations"
                    : snap.AsyncBacklogTotal > 0
                        ? $"ℹ {snap.AsyncBacklogTotal:N0} pending continuations"
                        : "—";
            string combined = starvation?.StarvationAdjustmentCount >= 5 && snap.AsyncBacklogTotal >= 10
                ? "🔴 Starvation confirmed"
                : snap.AsyncBacklogTotal >= 100 || starvation?.StarvationAdjustmentCount >= 3
                    ? "⚠ Async pressure"
                    : trSig != "—" || dumpSig != "—" ? "ℹ Monitor"
                    : "✓ OK";
            if (trSig != "—" || dumpSig != "—" || snap.AsyncBacklogTotal > 0 || starvation?.TotalEvents > 0)
                matrixRows.Add(["Async / Thread Pool", trSig, dumpSig, combined]);
        }

        // Exceptions
        if (exceptions?.TotalThrown > 0 || snap.ExceptionThreadCount > 0)
        {
            string trSig = exceptions?.TotalThrown >= 10_000
                ? $"🔴 Storm — {exceptions.TotalThrown:N0} thrown  ({exceptions.UniqueTypes} types)"
                : exceptions?.TotalThrown >= 1_000
                    ? $"⚠ High — {exceptions.TotalThrown:N0} thrown"
                    : exceptions?.TotalThrown > 0
                        ? $"ℹ {exceptions.TotalThrown:N0} thrown"
                        : "—";
            string topEx = snap.ExceptionCounts.Count > 0
                ? snap.ExceptionCounts[0].Name.Split('.').Last() : "";
            string dumpSig = snap.ExceptionThreadCount > 0
                ? $"{snap.ExceptionThreadCount} thread(s) holding live exceptions"
                  + (topEx.Length > 0 ? $"  •  top: {topEx}" : "")
                : "—";
            string combined = exceptions?.TotalThrown >= 10_000 && snap.ExceptionThreadCount > 0
                ? "🔴 Exception accumulation"
                : exceptions?.TotalThrown >= 1_000 ? "⚠ High exception rate"
                : "✓ OK";
            matrixRows.Add(["Exceptions", trSig, dumpSig, combined]);
        }

        // Contention / Threads
        if (contention?.TotalContentions > 0 || snap.BlockedThreadCount > 0)
        {
            string trSig = contention?.TotalContentions >= 500
                ? $"🔴 {contention.TotalContentions:N0} contentions  ({contention.TotalWaitMs:F0} ms total wait)"
                : contention?.TotalContentions > 0
                    ? $"⚠ {contention.TotalContentions:N0} contentions  ({contention.TotalWaitMs:F0} ms)"
                    : "—";
            string dumpSig = snap.BlockedThreadCount > 0
                ? $"{snap.BlockedThreadCount}/{snap.AliveThreadCount} threads blocked"
                : $"{snap.AliveThreadCount} threads — none blocked";
            string combined = contention?.TotalContentions >= 500 && snap.BlockedThreadCount >= 5
                ? "🔴 Contention confirmed"
                : snap.BlockedThreadCount >= 10 || contention?.TotalContentions >= 500
                    ? "⚠ Thread blocking"
                    : "✓ OK";
            matrixRows.Add(["Contention / Threads", trSig, dumpSig, combined]);
        }

        // HTTP
        if (http?.HasData == true)
        {
            string trSig = http.P99RequestMs >= 2000
                ? $"🔴 P99={http.P99RequestMs:F0} ms  ({http.TotalRequests:N0} requests)"
                : http.P99RequestMs >= 500
                    ? $"⚠ P99={http.P99RequestMs:F0} ms  ({http.TotalRequests:N0} requests)"
                    : http.TotalRequests > 0
                        ? $"✓ P99={http.P99RequestMs:F0} ms"
                        : "—";
            if (http.ErrorCount > 0)
                trSig += $"  •  {http.ErrorCount * 100.0 / Math.Max(1, http.TotalRequests):F1}% errors";
            string dumpSig = snap.AsyncBacklogTotal > 0
                ? $"Dump: {snap.AsyncBacklogTotal:N0} pending async continuations"
                : "—";
            string combined = http.P99RequestMs >= 2000 && snap.AsyncBacklogTotal >= 10
                ? "🔴 HTTP + async backlog"
                : http.P99RequestMs >= 2000 ? "⚠ Slow HTTP"
                : "✓ OK";
            matrixRows.Add(["HTTP", trSig, dumpSig, combined]);
        }

        // SQL / Connections
        if (sql?.HasData == true || snap.ConnectionCount > 0)
        {
            string trSig = sql?.HasData == true && sql.MaxCommandMs >= 1000
                ? $"⚠ Slowest {sql.MaxCommandMs:F0} ms  ({sql.SlowCommandCount} slow / {sql.TotalCommands:N0} total,  avg {sql.AvgCommandMs:F1} ms)"
                : sql?.HasData == true && sql.TotalCommands > 0
                    ? $"ℹ {sql.TotalCommands:N0} queries  (avg {sql.AvgCommandMs:F1} ms)"
                    : "—";
            string dumpSig = snap.ConnectionCount > 0
                ? $"{snap.ConnectionCount:N0} live DB connections on heap"
                : "—";
            string combined = sql?.HasData == true && sql.MaxCommandMs >= 1000 && snap.ConnectionCount > 10
                ? "⚠ Slow SQL + high connections"
                : sql?.HasData == true && sql.TotalCommands > 0 ? "ℹ Present"
                : dumpSig != "—" ? "ℹ Connections found" : "—";
            if (trSig != "—" || dumpSig != "—")
                matrixRows.Add(["SQL / Connections", trSig, dumpSig, combined]);
        }

        if (matrixRows.Count > 0)
            sink.Table(
                ["Area", "Trace Signal  (when)", "Dump Signal  (what)", "Combined Assessment"],
                matrixRows,
                caption: "🔴 = both sources corroborate the same problem  •  ⚠ = single-source signal  •  ✓ = no issue detected");

        // ── Section C: Heap Composition at Capture ────────────────────────────
        long heapTotal = snap.Gen0Bytes + snap.Gen1Bytes + snap.Gen2Bytes + snap.LohBytes + snap.PohBytes;
        if (heapTotal > 0)
        {
            sink.Section("Heap Composition at Capture", "combined-heap");

            var heapSegs = new List<(string, double)>();
            if (snap.Gen0Bytes > 0) heapSegs.Add(("Gen 0",  snap.Gen0Bytes));
            if (snap.Gen1Bytes > 0) heapSegs.Add(("Gen 1",  snap.Gen1Bytes));
            if (snap.Gen2Bytes > 0) heapSegs.Add(("Gen 2",  snap.Gen2Bytes));
            if (snap.LohBytes  > 0) heapSegs.Add(("LOH",    snap.LohBytes));
            if (snap.PohBytes  > 0) heapSegs.Add(("POH",    snap.PohBytes));
            long remainder = snap.TotalHeapBytes - heapTotal;
            if (remainder > 0)      heapSegs.Add(("Other",  remainder));

            sink.DonutChart(heapSegs,
                caption: "Managed heap distribution at dump capture time — a Gen2-dominant chart (>60%) typically indicates long-lived or leaked objects",
                centerText: $"{DumpHelpers.FormatSize(snap.TotalHeapBytes)}\nheap");

            var heapGauges = new List<(string, double, string)>();
            if (snap.FragmentationPct > 0)
                heapGauges.Add(("Heap fragmentation",    snap.FragmentationPct, "%"));
            if (snap.TotalHeapBytes > 0 && snap.Gen2Bytes > 0)
                heapGauges.Add(("Gen2 share of heap",    snap.Gen2Bytes * 100.0 / snap.TotalHeapBytes, "%"));
            if (snap.LohBytes > 0 && snap.LohFragmentationPct > 0)
                heapGauges.Add(("LOH fragmentation",     snap.LohFragmentationPct, "%"));
            if (heapGauges.Count > 0)
                sink.Gauges(heapGauges, barMax: 100.0);
        }

        // ── Section D: Performance Gauges ─────────────────────────────────────
        bool hasCpuStats = cpu?.Stats is { AvgCpuPct: > 0 };
        bool hasTpData   = snap.TpMaxWorkers > 0;
        bool hasGcRatio  = gc?.TotalPauseMs > 0 && cpu?.Stats?.TraceDurationMs > 0;

        if (hasCpuStats || hasTpData || hasGcRatio || snap.BlockedThreadCount > 0)
        {
            sink.Section("Performance Indicators", "combined-perf");

            var perfGauges = new List<(string, double, string)>();
            if (hasCpuStats)
            {
                perfGauges.Add(("Avg CPU (trace)",  cpu!.Stats!.AvgCpuPct,  "%"));
                perfGauges.Add(("Peak CPU (trace)", cpu!.Stats!.MaxCpuPct,   "%"));
            }
            if (hasTpData)
                perfGauges.Add(("Thread-pool saturation",
                    snap.TpActiveWorkers * 100.0 / snap.TpMaxWorkers, "%"));
            if (snap.AliveThreadCount > 0)
                perfGauges.Add(("Blocked thread ratio",
                    snap.BlockedThreadCount * 100.0 / snap.AliveThreadCount, "%"));
            if (hasGcRatio)
                perfGauges.Add(("GC pause / trace time",
                    gc!.TotalPauseMs / cpu!.Stats!.TraceDurationMs * 100.0, "%"));

            if (perfGauges.Count > 0)
                sink.Gauges(perfGauges, barMax: 100.0);
        }

        // ── Section E-extra: Timeline Signals (MultiSparkline) ────────────────
        {
            var timelineSeries = new List<(string Label, IReadOnlyList<double> Values, string? Unit)>();

            if (cpu?.SamplesTimeline?.Count > 1)
                timelineSeries.Add(("CPU samples/sec", cpu.SamplesTimeline, null));
            if (exceptions?.RateTimeline?.Count > 1)
                timelineSeries.Add(("Exceptions/sec", exceptions.RateTimeline, null));
            if (contention?.WaitTimeline?.Count > 1)
                timelineSeries.Add(("Lock wait ms/sec", contention.WaitTimeline, "ms"));
            if (async_?.ScheduleRateTimeline?.Count > 1)
                timelineSeries.Add(("Tasks scheduled/sec", async_.ScheduleRateTimeline, null));
            if (sql?.DurationTimeline?.Count > 1)
                timelineSeries.Add(("SQL duration ms/sec", sql.DurationTimeline, "ms"));

            if (timelineSeries.Count > 0)
            {
                sink.Section("Timeline Signals", "combined-timeline");
                sink.MultiSparkline(
                    timelineSeries,
                    caption: "Each row shows activity over the trace window — aligned to the same time axis for easy correlation");
            }
        }

        // ── Section E: Allocation Breakdown during Trace ──────────────────────
        if (alloc?.TopTypes?.Count > 0)
        {
            sink.Section("Allocation Breakdown  (Trace Window)", "combined-alloc");

            int takeN      = Math.Min(8, alloc.TopTypes.Count);
            var donutSegs  = alloc.TopTypes
                .Take(takeN)
                .Select(t => (TrimTypeName(t.TypeName, 42), (double)t.EstimatedBytes))
                .ToList();
            long shownBytes = donutSegs.Sum(s => (long)s.Item2);
            if (alloc.EstimatedTotalBytes > shownBytes)
                donutSegs.Add(("Others", alloc.EstimatedTotalBytes - shownBytes));

            sink.DonutChart(donutSegs,
                caption: $"Estimated allocation bytes by type during trace window — ~{DumpHelpers.FormatSize(alloc.EstimatedTotalBytes)} total (sampled every ~100 KB)",
                centerText: $"{DumpHelpers.FormatSize(alloc.EstimatedTotalBytes)}\nallocated");

            // Cross-match: allocated vs live — CompareBar gives an immediate visual diagnosis
            var dumpTypeLookup = snap.TopTypes
                .Take(30)
                .ToDictionary(
                    t => t.Name.Split('.').Last(),
                    t => t.TotalBytes,
                    StringComparer.OrdinalIgnoreCase);

            var cbarItems = alloc.TopTypes
                .Take(12)
                .Select(t =>
                {
                    string shortName = t.TypeName.Split('.').Last();
                    long liveBytes   = dumpTypeLookup.GetValueOrDefault(shortName, 0L);
                    return (TrimTypeName(t.TypeName, 44), (double)t.EstimatedBytes, (double)liveBytes);
                })
                .ToList();

            sink.CompareBar(
                cbarItems,
                labelA:    "Allocated  (trace)",
                labelB:    "Live bytes  (dump)",
                caption:   "Purple = estimated allocations during trace window  •  Green = live bytes in the dump  •  Both bars present = retention signal",
                valueMode: "size");
        }

        // ── Section F: Cross-Source Finding Scores ────────────────────────────
        if (crossFindings.Count > 0)
        {
            sink.Section("Cross-Source Finding Scores", "combined-scores");

            int warnCount = crossFindings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = crossFindings.Count - critCount - warnCount;

            sink.KeyValues([
                ("🔴 Critical", critCount > 0 ? critCount.ToString() : "—"),
                ("⚠ Warning",  warnCount > 0 ? warnCount.ToString() : "—"),
                ("ℹ Info",     infoCount > 0 ? infoCount.ToString() : "—"),
                ("Top finding", TrimTypeName(crossFindings[0].Category + "  —  " + crossFindings[0].Headline, 80)),
                ("Confidence",  crossFindings[0].ConfidenceLabel),
            ]);

            // Gauge bar per finding (top 8 by score) — gives an instant visual ranking
            sink.Gauges(
                crossFindings
                    .Take(8)
                    .Select(f => (
                        TrimTypeName(f.Category + ": " + f.Headline, 60),
                        (double)f.Score,
                        "/100"))
                    .ToList(),
                barMax: 100.0);
        }
    }

    private static string TrimTypeName(string name, int maxLen)
    {
        if (name.Length <= maxLen) return name;
        // Prefer the short unqualified name (after the last dot)
        int dot       = name.LastIndexOf('.');
        string simple = dot >= 0 ? name[(dot + 1)..] : name;
        if (simple.Length <= maxLen) return simple;
        return simple[..(maxLen - 1)] + "…";
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
