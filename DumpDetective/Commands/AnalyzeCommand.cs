using DumpDetective.Collectors;
using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Models;
using DumpDetective.Output;

using Spectre.Console;

using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class AnalyzeCommand
{
    private const string Help = """
        Usage: DumpDetective analyze <dump-file> [options]

        Single-pass full health report: memory, threads, async, leaks, event
        subscriptions, string duplicates, WCF, connections, and more.
        Each issue is scored and produces a 0-100 health score.

        Options:
          --full               Include string-duplicate and event-leak analysis (slower)
          -o, --output <file>  Write report to file (.md / .html / .txt)
          -h, --help           Show this help

        Example:
          DumpDetective analyze app.dmp
          DumpDetective analyze app.dmp --full --output report.html
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

        DumpSnapshot snap = null!;
        var sw = Stopwatch.StartNew();
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start($"Running {(full ? "full" : "lightweight")} analysis on {Markup.Escape(Path.GetFileName(dumpPath))}...", ctx =>
            {
                void Upd(string msg) =>
                    ctx.Status($"[dim]{Markup.Escape(Path.GetFileName(dumpPath))}[/]  {Markup.Escape(msg)}");
                snap = full
                    ? DumpCollector.CollectFull(dumpPath, Upd)
                    : DumpCollector.CollectLightweight(dumpPath, Upd);
            });
        AnsiConsole.MarkupLine($"[dim]  Analysis complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");

        using var sink = IRenderSink.Create(outputPath);
        RenderReport(snap, sink);

        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    // ── Renderer (used by both this command and TrendAnalysisCommand) ─────────

    internal static void RenderReport(DumpSnapshot s, IRenderSink sink)
    {
        string scoreColor = s.HealthScore >= 70 ? "green" : s.HealthScore >= 40 ? "yellow" : "red";
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
    }

    static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";
    static string FormatSize(long b) => DumpHelpers.FormatSize(b);
}
