using DumpDetective.Collectors;
using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Models;
using DumpDetective.Output;

using Spectre.Console;

using System.Diagnostics;

namespace DumpDetective.Commands;

// Scored health report for a single dump. Mini mode: lightweight summary across
// memory, threads, exceptions, async, leaks, handles, WCF, and connections.
// Full mode: scored summary plus all individual command reports embedded as chapters.
internal static class AnalyzeCommand
{
    private const string Help = """
        Usage: DumpDetective analyze <dump-file> [options]

        Scored health report for a single dump.

        Report modes
        ─────────────
          (default)  Mini report — lightweight scored summary:
                     memory, threads, exceptions, async backlog, leaks, handles, WCF,
                     connections, modules.  Fast (~heap walk once).

          --full     Full report — scored summary PLUS all 20+ individual command
                     reports embedded as chapters in one document.
                     Recommended with --output; significantly slower.

        Options:
          --full               Full combined report (scored summary + all sub-reports)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective analyze app.dmp
          DumpDetective analyze app.dmp --full --output full-report.html
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? dumpPath = null, outputPath = null;

        bool full = false;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length) outputPath = args[++i];
            else if (args[i] == "--full")  full = true;
            else if (!args[i].StartsWith('-') && dumpPath is null) dumpPath = args[i];
        }

        if (dumpPath is null)       { AnsiConsole.MarkupLine("[bold red]✗[/] dump file path required."); return 1; }
        if (!File.Exists(dumpPath)) { AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: {Markup.Escape(dumpPath)}"); return 1; }

        CommandBase.PrintAnalyzing(dumpPath);

        if (!full)
        {
            // ── Mini: single open, lightweight collection + scored summary ────
            DumpSnapshot snap = null!;
            var sw = Stopwatch.StartNew();
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .Start($"Running mini analysis — {Markup.Escape(Path.GetFileName(dumpPath))}...", spinCtx =>
                {
                    void Upd(string msg) =>
                        spinCtx.Status($"[dim]{Markup.Escape(Path.GetFileName(dumpPath))}[/]  {Markup.Escape(msg)}");
                    snap = DumpCollector.CollectLightweight(dumpPath, Upd);
                });
            AnsiConsole.MarkupLine($"[dim]  Collection complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");
            using var sink = IRenderSink.Create(outputPath);
            RenderReport(snap, sink);
            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            return 0;
        }

        // ── Full: open dump once — snapshot collection + all sub-reports ──────
        try
        {
            using var dumpCtx = DumpContext.Open(dumpPath);
            if (dumpCtx.ArchWarning is not null)
                AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(dumpCtx.ArchWarning)}[/]");
            DumpSnapshot snap = null!;
            var sw = Stopwatch.StartNew();
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .Start($"Running full analysis — {Markup.Escape(Path.GetFileName(dumpPath))}...", spinCtx =>
                {
                    void Upd(string msg) =>
                        spinCtx.Status($"[dim]{Markup.Escape(Path.GetFileName(dumpPath))}[/]  {Markup.Escape(msg)}");
                    snap = DumpCollector.CollectFull(dumpCtx, Upd);
                });
            AnsiConsole.MarkupLine($"[dim]  Collection complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");
            using var sink = IRenderSink.Create(outputPath);
            RenderReport(snap, sink);
            AnsiConsole.MarkupLine("[bold blue]Embedding detailed sub-reports:[/]");
            RenderEmbeddedReports(dumpCtx, sink);
            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Unexpected error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    // ── Embedded sub-reports (full mode only) ─────────────────────────────────

    internal static void RenderEmbeddedReports(DumpContext ctx, IRenderSink sink)
    {
        // Walk the heap exactly once and share the result across all 23 sub-commands.
        var snapSw = Stopwatch.StartNew();
        CommandBase.RunStatus("Building shared heap snapshot for sub-reports...", () => ctx.EnsureSnapshot());
        AnsiConsole.MarkupLine(
            $"[dim]  Snapshot: {ctx.Snapshot!.TotalObjects:N0} objects, " +
            $"{ctx.Snapshot.TypeStats.Count:N0} types, " +
            $"{ctx.Snapshot.InboundCounts.Count:N0} referenced addrs  " +
            $"({snapSw.Elapsed.TotalSeconds:F1}s)[/]");

        // Define each sub-report as a (label, render action) pair.
        // All 23 run in parallel — each writes to its own CaptureSink so there
        // is no shared-sink contention. Results are replayed in order afterward.
        const int Total = 23;
        var steps = new (string Label, Action<IRenderSink> Fn)[Total]
        {
            ("Heap Statistics",     s => HeapStatsCommand.Render(ctx, s, top: 100)),
            ("Gen Summary",         s => GenSummaryCommand.Render(ctx, s)),
            ("Heap Fragmentation",  s => HeapFragmentationCommand.Render(ctx, s)),
            ("Large Objects",       s => LargeObjectsCommand.Render(ctx, s, top: 100)),
            ("High-Refs Analysis",  s => HighRefsCommand.Render(ctx, s, top: 50, minRefs: 5)),
            ("Exception Analysis",  s => ExceptionAnalysisCommand.Render(ctx, s, top: 50, showStack: true)),
            ("Thread Analysis",     s => ThreadAnalysisCommand.Render(ctx, s, showStacks: true)),
            ("Thread Pool",         s => ThreadPoolCommand.Render(ctx, s)),
            ("Async Stacks",        s => AsyncStacksCommand.Render(ctx, s, top: 100)),
            ("Deadlock Detection",  s => DeadlockDetectionCommand.Render(ctx, s)),
            ("Finalizer Queue",     s => FinalizerQueueCommand.Render(ctx, s, top: 50)),
            ("Handle Table",        s => HandleTableCommand.Render(ctx, s)),
            ("Pinned Objects",      s => PinnedObjectsCommand.Render(ctx, s)),
            ("Weak Refs",           s => WeakRefsCommand.Render(ctx, s)),
            ("Static Refs",         s => StaticRefsCommand.Render(ctx, s)),
            ("Timer Leaks",         s => TimerLeaksCommand.Render(ctx, s)),
            ("Event Analysis",      s => EventAnalysisCommand.Render(ctx, s)),
            ("String Duplicates",   s => StringDuplicatesCommand.Render(ctx, s, top: 100, minCount: 2)),
            ("WCF Channels",        s => WcfChannelsCommand.Render(ctx, s)),
            ("Connection Pool",     s => ConnectionPoolCommand.Render(ctx, s)),
            ("HTTP Requests",       s => HttpRequestsCommand.Render(ctx, s)),
            ("Memory Leak Analysis",s => MemoryLeakCommand.Render(ctx, s, top: 30, minCount: 500, noRootTrace: false, inclSystem: false)),
            ("Module List",         s => ModuleListCommand.Render(ctx, s)),
        };

        var captures = new CaptureSink[Total];
        for (int i = 0; i < Total; i++) captures[i] = new CaptureSink();

        var overallSw = Stopwatch.StartNew();
        AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
            .Start(pCtx =>
            {
                var task = pCtx.AddTask($"[bold]Sub-reports[/]  [dim]0/{Total}[/]", maxValue: Total);

                Parallel.For(0, Total,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    i =>
                    {
                        // [ThreadStatic] flag — each worker suppresses its own Status spinners
                        // and PrintAnalyzing lines without interfering with other threads.
                        CommandBase.SuppressVerbose = true;
                        try
                        {
                            steps[i].Fn(captures[i]);
                                task.Increment(1);
                                int n = (int)task.Value;
                                task.Description = n >= Total
                                    ? $"[bold]Sub-reports[/]  [dim]{Total}/{Total}  Done[/]"
                                    : $"[bold]Sub-reports[/]  [dim]{n}/{Total}  {Markup.Escape(steps[i].Label)}[/]";
                        }
                        finally
                        {
                            CommandBase.SuppressVerbose = false;
                        }
                    });
            });

        AnsiConsole.MarkupLine($"[dim]  ✓ {Total}/{Total} sub-reports  ({overallSw.Elapsed.TotalSeconds:F1}s)[/]");

        // Replay all captured chapters into the real sink in their original order.
        for (int i = 0; i < Total; i++)
            ReportDocReplay.Replay(captures[i].GetDoc(), sink);
    }

    // ── Renderer (used by this command, TrendAnalysisCommand, and full mode) ──

    internal static void RenderReport(DumpSnapshot s, IRenderSink sink, bool includeHeader = true)
    {
        string scoreColor = s.HealthScore >= 70 ? "green" : s.HealthScore >= 40 ? "yellow" : "red";
        if (includeHeader)
            sink.Header(
                "Dump Detective — Analysis Report",
                $"{Path.GetFileName(s.DumpPath)}  |  {s.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {s.ClrVersion ?? "unknown"}  |  Score: {s.HealthScore}/100");

        // ── Findings ──────────────────────────────────────────────────────────
        sink.Section("Findings");
        foreach (var f in s.Findings)
        {
            var lvl = f.Severity switch
            {
                FindingSeverity.Critical => AlertLevel.Critical,
                FindingSeverity.Warning  => AlertLevel.Warning,
                _                        => AlertLevel.Info,
            };
            sink.Alert(lvl, $"[{f.Category}] {f.Headline}", f.Detail, f.Advice);
        }
        sink.KeyValues(
        [
            ("Health score", $"{s.HealthScore}/100  [{ScoreLabel(s.HealthScore)}]"),
            ("Mode",         s.IsFullMode ? "Full" : "Lightweight"),
        ]);

        // ── Score breakdown ────────────────────────────────────────────────────
        var breakdownRows = s.Findings
            .Where(f => f.Deduction > 0)
            .OrderByDescending(f => f.Deduction)
            .Select(f => new[]
            {
                f.Category,
                f.Headline.Length > 65 ? f.Headline[..62] + "…" : f.Headline,
                $"-{f.Deduction}",
            })
            .ToList();
        if (breakdownRows.Count > 0)
        {
            int totalDeducted = 100 - s.HealthScore;
            sink.Table(["Category", "Finding", "Pts Deducted"], breakdownRows,
                $"Score breakdown: {s.HealthScore}/100 (−{totalDeducted} total)");
        }

        // ── Memory ────────────────────────────────────────────────────────────
        sink.Section("Memory");
        sink.KeyValues(
        [
            ("Total heap",    FormatSize(s.TotalHeapBytes)),
            ("Gen0",          FormatSize(s.Gen0Bytes)),
            ("Gen1",          FormatSize(s.Gen1Bytes)),
            ("Gen2",          FormatSize(s.Gen2Bytes)),
            ("LOH",           FormatSize(s.LohBytes)),
            ("POH",           FormatSize(s.PohBytes)),
            ("Fragmentation", $"{s.FragmentationPct:F1}%"),
        ]);
        if (s.TopTypes.Count > 0)
            sink.Table(
                ["Type", "Count", "Total Size"],
                s.TopTypes.Take(25).Select(t =>
                    new[] { t.Name, t.Count.ToString("N0"), FormatSize(t.TotalBytes) }).ToList(),
                "Top types by size");

        // ── Threads & Thread Pool ─────────────────────────────────────────────
        sink.Section("Threads & Thread Pool");
        sink.KeyValues(
        [
            ("Total threads",   s.ThreadCount.ToString("N0")),
            ("Blocked",         s.BlockedThreadCount.ToString("N0")),
            ("With exception",  s.ExceptionThreadCount.ToString("N0")),
            ("TP active",       $"{s.TpActiveWorkers} / {s.TpMaxWorkers} max"),
            ("TP idle",         s.TpIdleWorkers.ToString()),
        ]);
        if (s.AsyncBacklogTotal > 0)
        {
            sink.KeyValues([("Async backlog", s.AsyncBacklogTotal.ToString("N0"))]);
            if (s.TopAsyncMethods.Count > 0)
                sink.Table(
                    ["Method", "Count"],
                    s.TopAsyncMethods.Take(10).Select(m => new[] { m.Method, m.Count.ToString("N0") }).ToList(),
                    "Top suspended async methods");
        }

        // ── Exceptions ────────────────────────────────────────────────────────
        if (s.ExceptionCounts.Count > 0)
        {
            sink.Section("Exceptions on Heap");
            sink.Table(
                ["Exception Type", "Count"],
                s.ExceptionCounts.Take(15).Select(e => new[] { e.Type, e.Count.ToString("N0") }).ToList());
        }

        // ── Leaks & Handles ───────────────────────────────────────────────────
        sink.Section("Leaks & Handles");
        sink.KeyValues(
        [
            ("Finalizer queue",  s.FinalizerQueueDepth.ToString("N0")),
            ("Pinned handles",   s.PinnedHandleCount.ToString("N0")),
            ("Weak handles",     s.WeakHandleCount.ToString("N0")),
            ("Strong handles",   s.StrongHandleCount.ToString("N0")),
            ("Timer objects",    s.TimerCount.ToString("N0")),
            ("WCF objects",      $"{s.WcfObjectCount:N0}  (faulted: {s.WcfFaultedCount:N0})"),
            ("DB connections",   s.ConnectionCount.ToString("N0")),
        ]);
        if (s.TopFinalizerTypes.Count > 0)
            sink.Table(
                ["Type", "Count"],
                s.TopFinalizerTypes.Select(t => new[] { t.Type, t.Count.ToString("N0") }).ToList(),
                "Top finalizer queue types");

        // ── Event Leaks (full mode) ───────────────────────────────────────────
        if (s.IsFullMode)
        {
            sink.Section("Event Leaks");
            sink.KeyValues(
            [
                ("Leak fields",      s.EventLeakFieldCount.ToString("N0")),
                ("Total subscribers",s.EventSubscriberTotal.ToString("N0")),
                ("Max on one field", s.EventLeakMaxOnField.ToString("N0")),
            ]);
            if (s.TopEventLeaks.Count > 0)
                sink.Table(
                    ["Publisher Type", "Field", "Subscribers"],
                    s.TopEventLeaks.Select(e =>
                        new[] { e.PublisherType, e.FieldName, e.Subscribers.ToString("N0") }).ToList(),
                    "Top event leak fields");

            // ── String duplicates ─────────────────────────────────────────────
            sink.Section("String Duplicates");
            sink.KeyValues(
            [
                ("Duplicate groups", s.StringDuplicateGroups.ToString("N0")),
                ("Wasted bytes",     FormatSize(s.StringWastedBytes)),
                ("Total string bytes",FormatSize(s.StringTotalBytes)),
            ]);
        }

        // ── Modules ───────────────────────────────────────────────────────────
        sink.Section("Modules");
        sink.KeyValues(
        [
            ("Total assemblies",  s.ModuleCount.ToString("N0")),
            ("App assemblies",    s.AppModuleCount.ToString("N0")),
            ("System/framework",  (s.ModuleCount - s.AppModuleCount).ToString("N0")),
        ]);
        // ── Memory Leak Analysis ──────────────────────────────────────────
        sink.Section("Memory Leak Analysis");
        {
            double gen2Pct  = s.TotalHeapBytes > 0 ? s.Gen2Bytes * 100.0 / s.TotalHeapBytes : 0;
            var    strType  = s.TopTypes.FirstOrDefault(t => t.Name == "System.String");

            sink.KeyValues(
            [
                ("Gen2 % of heap",   $"{gen2Pct:F1}%  ({FormatSize(s.Gen2Bytes)})"),
                ("LOH",              FormatSize(s.LohBytes)),
                ("Total objects",    s.TotalObjectCount.ToString("N0")),
                ("System.String",    strType is not null
                                         ? $"{strType.Count:N0}  ({FormatSize(strType.TotalBytes)})"
                                         : "—"),
            ]);

            // Advisory alert based on Gen2 dominance — shown BEFORE the type table
            if (gen2Pct > 50)
                sink.Alert(AlertLevel.Critical,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap",
                    detail: "Objects are surviving multiple GC cycles — strong managed memory leak signal.",
                    advice: "Run: memory-leak <dump>  for full suspect analysis with GC root chains.");
            else if (gen2Pct > 30)
                sink.Alert(AlertLevel.Warning,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap",
                    detail: "Gen2 is elevated. Monitor for growth across multiple dumps.",
                    advice: "Run: trend-analysis <d1> <d2> to confirm growth, then memory-leak <dump>.");
            else
                sink.Alert(AlertLevel.Info,
                    $"Gen2 holds {gen2Pct:F1}% of managed heap — within normal range.",
                    advice: "Run: memory-leak <dump>  for a deeper investigation if growth is suspected.");

            // Top accumulation suspects: high-count or large types
            var suspects = s.TopTypes
                .Where(t => t.Count >= 1_000 || t.TotalBytes >= 1_000_000)
                .OrderByDescending(t => t.Count)
                .Take(15)
                .ToList();

            if (suspects.Count > 0)
                sink.Table(
                    ["Type", "Count", "Total Size"],
                    suspects.Select(t =>
                        new[] { t.Name, t.Count.ToString("N0"), FormatSize(t.TotalBytes) }).ToList(),
                    "High-count / large types — accumulating types are leak suspects. " +
                    "Run 'memory-leak <dump>' for full Gen2/LOH breakdown + GC root chains.");
        }    }

    static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";
    static string FormatSize(long b) => DumpHelpers.FormatSize(b);
}
