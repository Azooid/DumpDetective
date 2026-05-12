using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using DumpDetective.Analysis.Trace;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;
using CmdData = DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Opens a trace file once and runs all trace sub-analyzers sequentially,
/// producing a single combined report with one chapter per analyzer.
///
/// Sub-analyzers: cpu-trace, alloc-trace, gc-trace, contention-trace,
///                exceptions-trace, thread-pool-starvation, jit-trace, http-trace,
///                async-trace, sql-trace
/// </summary>
public sealed class TraceAnalyzeCommand : ICommand
{
    private static readonly (string Heading, string[] Names)[] s_traceGroups =
    [
        ("CPU & Allocation",         ["cpu-trace", "alloc-trace", "alloc-burst-trace"]),
        ("GC & Memory",              ["gc-trace", "finalizer-trace", "loh-trace"]),
        ("Exceptions & Locks",       ["exceptions-trace", "contention-trace", "deadlock-trace", "retry-storm-trace"]),
        ("Threads & Concurrency",    ["threadpool-starvation", "async-trace", "context-switch-trace", "task-scheduler-trace"]),
        ("JIT & HTTP",               ["jit-trace", "http-trace", "kestrel-trace", "aspnetcore-pipeline-trace"]),
        ("SQL & Network",            ["sql-trace", "json-trace", "connection-pool-trace", "socket-trace", "dns-trace"]),
        ("Infrastructure",           ["process-lifecycle-trace", "file-io-trace", "handle-leak-trace"]),
        ("Observability",            ["otel-trace"]),
        ("Intelligence",             ["anomaly-trace", "root-cause-trace"]),
    ];

    private readonly IReadOnlyList<ITraceSubAnalyzer> _subAnalyzers;

    public TraceAnalyzeCommand(IReadOnlyList<ITraceSubAnalyzer> subAnalyzers)
    {
        _subAnalyzers = subAnalyzers;
    }

    public string Name               => "trace-analyze";
    public string Description        => "Full trace analysis — opens trace once and runs all 29 sub-analyzers: cpu, alloc, gc, exceptions, contention, thread-pool-starvation, jit, http, async, sql, json, context-switch, finalizer, connection-pool, alloc-burst, deadlock, loh, retry-storm, process-lifecycle, task-scheduler, file-io, socket, dns, kestrel, aspnetcore-pipeline, otel, handle-leak, anomaly, root-cause.";
    public bool   IncludeInFullAnalyze => false; // requires a trace file, not a .dmp

    private const string Help = """
        Usage: DumpDetective trace-analyze <trace-file> [options]

        Opens the trace file ONCE and runs all trace sub-analyzers in sequence,
        producing a single combined report with a summary dashboard at the top:

          • cpu-trace             CPU hot-path, top methods, call tree
          • alloc-trace           Top allocating types and call sites
          • gc-trace              GC pause times, generations, trigger reasons
          • contention-trace      Lock contention hotspots and wait times
          • exceptions-trace      Exception flood patterns by type
          • thread-pool-starvation  ThreadPool starvation signals and adjustments
          • jit-trace             JIT compilation overhead, hot modules
          • http-trace            HTTP request latency, error rate, top paths

        Supported input formats:
          .nettrace    EventPipe trace collected with a suitable profile
          .etl         Windows ETW trace

        Options:
          -n, --top <N>            Top N items per section (default: 20)
          --process <name>         Filter to a specific process name
          --show-system            Include system/kernel frames in CPU tree (default: hidden)
          --show-unresolved        Include unresolved frames in CPU tree (default: hidden)
          --slow-ms <ms>           HTTP slow-request threshold in ms (default: 1000)
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective trace-analyze app.nettrace
                    DumpDetective trace-analyze perf.etl --process w3wp --output report.html
          DumpDetective trace-analyze app.nettrace --top 30 --show-system
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a             = CliArgs.Parse(args);
        int     top           = a.GetInt("top", 100);
        double  slowMs        = a.GetInt("slow-ms", 1000);
        string? tracePath     = a.DumpPath ?? a.Positionals.FirstOrDefault();
        string? processFilter = a.GetOption("process");
        bool    filterSystem  = !a.HasFlag("show-system");
        bool    filterUnresolved = !a.HasFlag("show-unresolved");

        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace or .etl).");
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
            AnsiConsole.MarkupLine($"[bold red]✗[/] Unsupported file type. Expected .nettrace or .etl — got: {Markup.Escape(Path.GetFileName(tracePath))}");
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
        if (traceCompanions.Count > 0)
            AnsiConsole.MarkupLine($"[dim]  + {traceCompanions.Count} companion file(s) will be auto-merged: {Markup.Escape(string.Join(", ", traceCompanions))}[/]");

        TraceLog? trace = null;
        try
        {
            CommandBase.RunStatus("Opening trace file...", update =>
                trace = TraceOpener.Open(tracePath, s => update($"{Name}  {s}")));

            string traceFileName = Path.GetFileName(tracePath);
            var outputPaths = a.EffectiveOutputPaths.Count > 0
                ? a.EffectiveOutputPaths
                : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath!, ".html")];
            using var sink = SinkFactory.CreateMulti(outputPaths);
            var captured = new Dictionary<string, ReportDoc>(StringComparer.OrdinalIgnoreCase);
            var results  = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var runParams = new TraceRunParams(top, processFilter, filterSystem, filterUnresolved, slowMs);

            sink.Header("Trace Analysis",
                $"File: {traceFileName}" +
                (processFilter is not null ? $" | Process: {processFilter}" : ""),
                navLevel: 1);

            // ── Phase 1: run all sub-analyzers ──────────────────────────────────
            foreach (var sub in _subAnalyzers)
            {
                if (sub.HasCorrelationPhase) continue;
                string? traceInfo = null;
                RunAnalyzer(sub.Key,
                    update => traceInfo = sub.Run(trace!, traceFileName, runParams, captured, results, s => update($"{sub.Key}  {s}")),
                    () => traceInfo);
            }

            // ── Phase 2: data extraction for cross-cutting sections ──────────────
            var cpuData        = results.GetValueOrDefault("cpu-trace")              as CmdData.CpuTraceData;
            var allocData      = results.GetValueOrDefault("alloc-trace")            as CmdData.AllocTraceData;
            var gcData         = results.GetValueOrDefault("gc-trace")               as CmdData.GcTraceData;
            var contentionData = results.GetValueOrDefault("contention-trace")       as CmdData.ContentionTraceData;
            var exceptionsData = results.GetValueOrDefault("exceptions-trace")       as CmdData.ExceptionsTraceData;
            var starvationData = results.GetValueOrDefault("thread-pool-starvation") as CmdData.ThreadPoolStarvationData;
            var jitData        = results.GetValueOrDefault("jit-trace")              as CmdData.JitTraceData;
            var httpData       = results.GetValueOrDefault("http-trace")             as CmdData.HttpTraceData;
            var asyncData      = results.GetValueOrDefault("async-trace")            as CmdData.AsyncTraceData;
            var sqlData        = results.GetValueOrDefault("sql-trace")              as CmdData.SqlTraceData;

            // ── Trace Summary Dashboard ────────────────────────────────────────
            // Rendered FIRST so the reader gets a cross-cutting health overview
            // before diving into per-analyzer chapters.
            RenderTraceSummary(sink, traceFileName, cpuData, allocData, gcData,
                contentionData, exceptionsData, starvationData, jitData, httpData);

            // ── Unified Timeline ──────────────────────────────────────────────
            // Cross-analyzer chronological event map — surfaces correlated events
            // from GC, Contention, Exceptions, HTTP, SQL, Async in one view.
            var timelineSlices = TraceTimelineBuilder.Build(
                gcData, contentionData, exceptionsData, httpData, sqlData, asyncData);
            RenderTimeline(sink, timelineSlices);

            // ── Cross-Analyzer Correlation ────────────────────────────────────
            // Runs after all analyzers complete — derives causal relationships
            // between signals that no single analyzer can see in isolation.
            var correlations = CorrelationEngine.Correlate(
                cpuData, allocData, gcData, contentionData, exceptionsData,
                starvationData, jitData, httpData, asyncData, sqlData);
            RenderCorrelationFindings(sink, correlations);

            // ── Diagnostic Interpretation ─────────────────────────────────────
            // Sourced from the CPU semantic pipeline which has the richest signal.
            RenderDiagnosticInterpretation(sink, cpuData);

            // ── Phase 3: root-cause (runs after correlations) ──────────────────
            foreach (var sub in _subAnalyzers)
            {
                if (!sub.HasCorrelationPhase) continue;
                string? traceInfo = null;
                RunAnalyzer(sub.Key,
                    update => traceInfo = sub.OnCorrelationAvailable(traceFileName, correlations, captured, results, top),
                    () => traceInfo);
            }

            // Grouped replay improves report navigation visibility while preserving
            // analyzer run order above.
            foreach (var (heading, names) in s_traceGroups)
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
            if (outputPaths.All(p => p.Equals("console", StringComparison.OrdinalIgnoreCase)) && sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {ProgressLogger.FileLink(sink.FilePath)}");

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
    // Trace Summary Dashboard — shown at the very top of the combined report
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderTraceSummary(
        IRenderSink sink,
        string traceFileName,
        CmdData.CpuTraceData?              cpu,
        CmdData.AllocTraceData?            alloc,
        CmdData.GcTraceData?               gc,
        CmdData.ContentionTraceData?       contention,
        CmdData.ExceptionsTraceData?       exceptions,
        CmdData.ThreadPoolStarvationData?  starvation,
        CmdData.JitTraceData?              jit,
        CmdData.HttpTraceData?             http)
    {
        sink.Header("Trace Summary", "", navLevel: 2);
        sink.Section("Overview", "summary-overview");

        // ── Key value pairs: identity + CPU stats ─────────────────────────────
        string duration = "";
        string avgCpu   = "";
        string peakCpu  = "";
        string process  = cpu?.FilteredProcess ?? "(all)";
        if (cpu?.Stats is { } s)
        {
            duration = s.TraceDurationMs >= 60_000
                ? $"{s.TraceDurationMs / 60_000:F1} min"
                : $"{s.TraceDurationMs / 1000:F1} s";
            avgCpu   = $"{s.AvgCpuPct:F1}%";
            peakCpu  = $"{s.MaxCpuPct:F1}%";
            process  = s.TopProcessName.Length > 0 ? s.TopProcessName : process;
        }

        sink.KeyValues([
            ("File",              traceFileName),
            ("Process",           process),
            ("Trace duration",    duration.Length > 0 ? duration : "—"),
            ("CPU samples",       cpu?.TotalSamples > 0 ? cpu.TotalSamples.ToString("N0") : "—"),
            ("Avg CPU",           avgCpu.Length > 0 ? avgCpu : "—"),
            ("Peak CPU",          peakCpu.Length > 0 ? peakCpu : "—"),
            ("Allocations (est)", alloc?.EstimatedTotalBytes > 0
                ? FormatBytes(alloc.EstimatedTotalBytes) : "—"),
            ("Top alloc type",    alloc?.TopTypes.Count > 0
                ? TrimTypeName(alloc.TopTypes[0].TypeName, 50) : "—"),
            ("GC events",         gc?.TotalGcs > 0 ? gc.TotalGcs.ToString("N0") : "—"),
            ("Max GC pause",      gc?.MaxPauseMs > 0 ? $"{gc.MaxPauseMs:F1} ms" : "—"),
            ("Total GC pause",    gc?.TotalPauseMs > 0 ? $"{gc.TotalPauseMs:F0} ms" : "—"),
            ("Exceptions thrown", exceptions?.TotalThrown > 0 ? exceptions.TotalThrown.ToString("N0") : "—"),
            ("Exception types",   exceptions?.UniqueTypes > 0 ? exceptions.UniqueTypes.ToString("N0") : "—"),
            ("Lock contentions",  contention?.TotalContentions > 0 ? contention.TotalContentions.ToString("N0") : "—"),
            ("Total wait time",   contention?.TotalWaitMs > 0 ? $"{contention.TotalWaitMs:F1} ms" : "—"),
            ("TP starvation adj", starvation?.StarvationAdjustmentCount > 0
                ? starvation.StarvationAdjustmentCount.ToString("N0") : "—"),
            ("Methods JIT'd",     jit?.TotalMethodsJitted > 0 ? jit.TotalMethodsJitted.ToString("N0") : "—"),
            ("Total JIT time",    jit?.TotalJitTimeMs > 0 ? $"{jit.TotalJitTimeMs:F0} ms" : "—"),
            ("HTTP requests",     http?.HasData == true && http.TotalRequests > 0
                ? http.TotalRequests.ToString("N0") : "—"),
            ("HTTP P99 latency",  http?.HasData == true && http.P99RequestMs > 0
                ? $"{http.P99RequestMs:F0} ms" : "—"),
            ("HTTP errors",       http?.HasData == true && http.ErrorCount > 0
                ? $"{http.ErrorCount:N0} ({http.ErrorCount * 100.0 / Math.Max(1, http.TotalRequests):F1}%)" : "—"),
        ]);

        // ── Gauges: CPU utilisation at a glance ───────────────────────────────
        if (cpu?.Stats is { } stats && stats.AvgCpuPct > 0)
        {
            sink.Gauges([
                ("Avg CPU",   stats.AvgCpuPct,  "%"),
                ("Peak CPU",  stats.MaxCpuPct,   "%"),
            ], barMax: 100.0);
        }

        // ── Cross-cutting signal table ─────────────────────────────────────────
        sink.Section("Signal Summary", "summary-signals");
        var signals = new List<string[]>();

        if (cpu?.Stats is { AvgCpuPct: >= 70 } cpuStats)
            signals.Add(["CPU", "⚠ High", $"Avg {cpuStats.AvgCpuPct:F1}% CPU — see CPU Trace chapter", "cpu-trace"]);
        else if (cpu?.Stats is { MaxCpuPct: >= 90 } cpuPeak)
            signals.Add(["CPU", "⚠ Spike", $"Peak {cpuPeak.MaxCpuPct:F1}% CPU — see CPU Trace chapter", "cpu-trace"]);
        else if (cpu?.TotalSamples > 0)
            signals.Add(["CPU", "✓ OK", $"Avg {avgCpu}", "cpu-trace"]);

        if (gc?.MaxPauseMs >= 200)
            signals.Add(["GC", "⚠ High pause", $"Max GC pause {gc.MaxPauseMs:F0} ms — see GC Trace chapter", "gc-trace"]);
        else if (gc?.TotalGcs > 0)
            signals.Add(["GC", "✓ OK", $"{gc.TotalGcs:N0} GCs, max pause {gc.MaxPauseMs:F1} ms", "gc-trace"]);

        if (alloc?.EstimatedTotalBytes > 0)
        {
            string allocLabel = alloc.EstimatedTotalBytes > 5L * 1024 * 1024 * 1024
                ? "⚠ High" : "ℹ Present";
            signals.Add(["Allocations", allocLabel, $"~{FormatBytes(alloc.EstimatedTotalBytes)} allocated", "alloc-trace"]);
        }

        if (exceptions?.TotalThrown >= 10_000)
            signals.Add(["Exceptions", "⚠ Flood", $"{exceptions.TotalThrown:N0} thrown across {exceptions.UniqueTypes} types", "exceptions-trace"]);
        else if (exceptions?.TotalThrown > 0)
            signals.Add(["Exceptions", "ℹ Present", $"{exceptions.TotalThrown:N0} thrown", "exceptions-trace"]);

        if (contention?.TotalContentions >= 500)
            signals.Add(["Contention", "⚠ High", $"{contention.TotalContentions:N0} contentions, {contention.TotalWaitMs:F0} ms wait", "contention-trace"]);
        else if (contention?.TotalContentions > 0)
            signals.Add(["Contention", "ℹ Present", $"{contention.TotalContentions:N0} contentions", "contention-trace"]);

        if (starvation?.StarvationAdjustmentCount >= 3)
            signals.Add(["Thread Pool", "⚠ Starvation", $"{starvation.StarvationAdjustmentCount} starvation adjustments detected", "thread-pool-starvation"]);
        else if (starvation?.TotalEvents > 0)
            signals.Add(["Thread Pool", "✓ OK", "No starvation adjustments detected", "thread-pool-starvation"]);

        if (jit?.TotalMethodsJitted > 0)
            signals.Add(["JIT", "ℹ Present", $"{jit.TotalMethodsJitted:N0} methods compiled", "jit-trace"]);

        if (http?.HasData == true && http.TotalRequests > 0)
        {
            double errPct = http.ErrorCount * 100.0 / Math.Max(1, http.TotalRequests);
            string httpStatus = errPct >= 5 ? "⚠ Errors" : (http.P99RequestMs >= 2000 ? "⚠ Slow" : "✓ OK");
            signals.Add(["HTTP", httpStatus,
                $"{http.TotalRequests:N0} req, P99={http.P99RequestMs:F0} ms, {errPct:F1}% errors", "http-trace"]);
        }

        // Semantic detector signals (from CPU pipeline)
        if (cpu?.SemanticFindings is { Count: > 0 } detectorFindings)
        {
            foreach (var f in detectorFindings)
            {
                string status = f.Score >= 60 ? "⚠ Detected" : "ℹ Possible";
                signals.Add(["Pattern Detector", status, $"{f.Category}: {f.Headline}", "interp-findings"]);
            }
        }

        if (signals.Count > 0)
            sink.Table(
                ["Area", "Status", "Detail", "Chapter"],
                signals,
                "Cross-cutting signal summary — click the Chapter column to jump to the detailed section");
        else
            sink.Alert(AlertLevel.Info, "No signal data available — all analyzers returned empty results.");

        // ── Top-level recommendations ─────────────────────────────────────────
        // Emit a ranked list of actionable recommendations based on the signal data
        var recs = new List<string>();

        if (cpu?.Stats is { AvgCpuPct: >= 70 } || cpu?.Stats is { MaxCpuPct: >= 90 })
            recs.Add("1. CPU is saturated — investigate top exclusive methods in the CPU Trace chapter.");
        if (gc?.MaxPauseMs >= 200)
            recs.Add($"{recs.Count + 1}. GC pause of {gc.MaxPauseMs:F0} ms detected — check for Gen2/LOH promotions in the GC Trace chapter.");
        if (exceptions?.TotalThrown >= 10_000)
            recs.Add($"{recs.Count + 1}. Exception flood ({exceptions.TotalThrown:N0} exceptions) — exceptions-as-control-flow is expensive. Review the Exceptions chapter.");
        if (contention?.TotalContentions >= 500)
            recs.Add($"{recs.Count + 1}. High lock contention — {contention.TotalContentions:N0} contentions. Review the Contention chapter for hot lock sites.");
        if (starvation?.StarvationAdjustmentCount >= 3)
            recs.Add($"{recs.Count + 1}. ThreadPool starvation — {starvation.StarvationAdjustmentCount} adjustments. Check for blocking calls on thread pool threads.");
        if (http?.HasData == true && http.P99RequestMs >= 2000)
            recs.Add($"{recs.Count + 1}. HTTP P99 latency is {http.P99RequestMs:F0} ms — correlate with GC pauses and lock contention timings.");
        if (cpu?.SemanticFindings is { Count: > 0 } topDetectors)
        {
            var top1 = topDetectors.OrderByDescending(f => f.Score).FirstOrDefault();
            if (top1 is not null && top1.Score >= 60)
                recs.Add($"{recs.Count + 1}. Pattern detector '{top1.Category}: {top1.Headline}' (score {top1.Score}) — see Diagnostic Interpretation chapter.");
        }

        if (recs.Count > 0)
        {
            sink.Section("Top Recommendations", "summary-recommendations");
            sink.Alert(AlertLevel.Info,
                string.Join("\n", recs),
                detail: "These recommendations are ordered by likely performance impact. Each links to the relevant chapter in this report.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (1.0 * (1 << 30)):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (1.0 * (1 << 20)):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (1.0 * (1 << 10)):F1} KB";
        return $"{bytes} B";
    }

    private static string TrimTypeName(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];

    // ─────────────────────────────────────────────────────────────────────────
    // Cross-Analyzer Correlation Findings
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderCorrelationFindings(
        IRenderSink                      sink,
        IReadOnlyList<CorrelationFinding> findings)
    {
        if (findings.Count == 0) return;

        sink.Header("Correlation Analysis", "", navLevel: 2);
        sink.Section("Findings", "correlation-findings");
        sink.Alert(AlertLevel.Info,
            "The following findings are derived from cross-analyzer correlation — " +
            "each represents a causal relationship between two or more signals that no single analyzer sees in isolation. " +
            "Findings are ranked by score (0–100).",
            detail: null);

        // Summary table: one row per finding
        var rows = new List<string[]>(findings.Count);
        foreach (var f in findings)
        {
            string severity = f.Severity switch
            {
                FindingSeverity.Critical => "🔴 Critical",
                FindingSeverity.Warning  => "⚠ Warning",
                _                       => "ℹ Info"
            };
            rows.Add([severity, f.Category, $"{f.Score}/100", f.Headline,
                      string.Join(", ", f.ContributingAreas)]);
        }
        sink.Table(
            ["Severity", "Category", "Score", "Headline", "Contributing Analyzers"],
            rows,
            "Cross-analyzer causal findings — ranked by score");

        // Detailed finding blocks
        foreach (var f in findings)
        {
            var level = f.Severity switch
            {
                FindingSeverity.Critical => AlertLevel.Critical,
                FindingSeverity.Warning  => AlertLevel.Warning,
                _                       => AlertLevel.Info
            };
            sink.Alert(level,
                $"[{f.Category}] {f.Headline}  (score: {f.Score}/100)",
                f.Detail,
                f.Advice);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Diagnostic Interpretation (CPU semantic findings + hot chains)
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderDiagnosticInterpretation(
        IRenderSink sink,
        CmdData.CpuTraceData? cpuData)
    {
        // Only render if the semantic pipeline produced findings or hot chains
        bool hasFindings = cpuData?.SemanticFindings is { Count: > 0 };
        bool hasChains   = cpuData?.HotChains is { Count: > 0 };
        if (!hasFindings && !hasChains)
            return;

        sink.Header("Diagnostic Interpretation", "", navLevel: 2);
        sink.Section("Summary", "interp-summary");
        sink.Alert(AlertLevel.Info,
            "The following patterns were automatically identified from the CPU call tree. " +
            "This section provides root-cause context before the detailed per-analyzer chapters.",
            detail: "Findings are ranked by score (0–100). Hot chains show the path from entry point to the executing bottleneck.");

        // ── Category Scores ───────────────────────────────────────────────────
        if (cpuData?.CategoryScores is { Count: > 0 } scores)
        {
            sink.Section("Pattern Category Scores", "interp-scores");
            var rows = new List<string[]>(scores.Count);
            for (int i = 0; i < scores.Count; i++)
            {
                var s = scores[i];
                string urgency = s.Score switch { >= 85 => "High", >= 65 => "Medium", _ => "Low" };
                rows.Add([s.Category, $"{s.Score}/100", urgency,
                          s.FindingCount.ToString(), s.TopFinding.Headline]);
            }
            sink.Table(
                ["Category", "Score", "Urgency", "Findings", "Top Finding"],
                rows,
                "Ranked by score — focus on High urgency categories first");
        }

        // ── Ranked Findings ───────────────────────────────────────────────────
        if (cpuData?.SemanticFindings is { Count: > 0 } findings)
        {
            sink.Section("Ranked Findings", "interp-findings");
            for (int i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                var level = f.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                       => AlertLevel.Info
                };
                sink.Alert(level,
                    $"[{f.Category}] {f.Headline}  (score: {f.Score}/100)",
                    f.Detail,
                    f.Advice);
            }
        }

        // ── Hot Chains ─────────────────────────────────────────────────────────
        if (cpuData?.HotChains is { Count: > 0 } chains)
        {
            sink.Section("Hot Chains", "interp-chains");
            var rows = new List<string[]>(Math.Min(chains.Count, 5));
            for (int i = 0; i < Math.Min(chains.Count, 5); i++)
            {
                var c = chains[i];
                var chainParts = new string[c.Chain.Count];
                for (int j = 0; j < c.Chain.Count; j++) chainParts[j] = c.Chain[j];
                string chainStr = string.Join(" → ", chainParts);
                rows.Add([$"{c.OwnerPct:F1}%", c.RootMethod, c.ExclusiveHotMethod, $"{c.HotPct:F1}%", chainStr]);
            }
            sink.Table(
                ["Owns (Incl%)", "Entry Point", "Hot Leaf", "Hot Leaf (Excl%)", "Chain"],
                rows,
                $"Top {rows.Count} hot chain(s)");
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning,
            "trace-analyze requires a trace file (.nettrace or .etl) — it cannot analyze a memory dump.");

    // ─────────────────────────────────────────────────────────────────────────
    // Unified Timeline
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderTimeline(
        IRenderSink                      sink,
        IReadOnlyList<TimelineSlice>     slices)
    {
        if (slices.Count == 0) return;

        sink.Header("Unified Timeline", "", navLevel: 2);
        sink.Section("Cross-Analyzer Event Timeline", "timeline-events");
        sink.Alert(AlertLevel.Info,
            $"Showing {slices.Count} significant events from all analyzers, ordered chronologically. " +
            "Critical and long-duration events are prioritised when the count exceeds the display limit.",
            detail: "Categories: GC · Contention · Exception · HTTP · SQL · Async. " +
                    "Severity: 🔴 Critical  ⚠ Warning  ℹ Info");

        var rows = new List<string[]>(slices.Count);
        foreach (var s in slices)
        {
            string sevIcon = s.Severity switch
            {
                FindingSeverity.Critical => "🔴",
                FindingSeverity.Warning  => "⚠",
                _                       => "ℹ"
            };
            string start    = $"{s.StartMs:F0} ms";
            string duration = s.DurationMs >= 1 ? $"{s.DurationMs:F0} ms" : "<1 ms";
            rows.Add([start, duration, s.Category, sevIcon, s.Label]);
        }
        sink.Table(
            ["Start", "Duration", "Category", "Sev", "Event"],
            rows,
            "Chronological cross-analyzer event timeline — sorted by start time");
    }
}
