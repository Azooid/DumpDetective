using System.Diagnostics;
using DumpDetective.Analysis;

namespace DumpDetective.Commands;

/// <summary>
/// Analyzes multiple dumps and reports memory/leak trends over time.
/// </summary>
public sealed class TrendAnalysisCommand : ICommand
{
    public string Name               => "trend-analysis";
    public string Description        => "Analyze multiple dumps for memory/leak trends (--full for sub-reports).";
    public bool   IncludeInFullAnalyze => false; // multi-dump, not nested

    private const string Help = """
        Usage: DumpDetective trend-analysis <dump1> <dump2> [<dump3> ...] [options]
               DumpDetective trend-analysis <dump-directory> [options]
               DumpDetective trend-analysis --list <paths.txt> [options]

        Analyzes multiple dumps and reports memory/leak trends over time.
        Dumps are sorted by file modification time to establish the timeline.

        Options:
          --list <file>          Read dump paths from a text file (one path per line)
          --full                 Run full collection per dump (includes event leaks,
                                 string duplicates — slower but more data)
          --baseline <n>         1-based index of the dump to use as baseline for
                                 trend arrows and comparisons (default: 1 = first dump)
          --ignore-event <type>  Exclude publisher types containing <type> from the
                                 Event Leak Analysis table. Repeatable.
          -o, --output <f>       Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help

        Examples:
          DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
          DumpDetective trend-analysis D:\\dumps --output trends.html
          DumpDetective trend-analysis --list dumps.txt --full --output report.md
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (a.Help || args.Length == 0)
        {
            AnsiConsole.Write(new Markup(Help));
            return 0;
        }

        var inputs       = a.Positionals.ToList();
        bool full        = a.HasFlag("full");
        var ignoreEvents = a.GetAll("ignore-event").ToList();
        int baselineArg  = a.GetInt("baseline", 1);
        if (baselineArg < 1)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] --baseline must be a positive integer.");
            return 1;
        }

        var listFile = a.GetOption("list");
        if (listFile is not null)
        {
            if (!File.Exists(listFile))
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] list file not found: {Markup.Escape(listFile)}");
                return 1;
            }
            inputs.AddRange(
                File.ReadAllLines(listFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var dumpPaths = ExpandDumpInputs(inputs, out var missingPaths, out var invalidDumpFiles);

        if (missingPaths.Count > 0)
        {
            foreach (var p in missingPaths)
                AnsiConsole.MarkupLine($"[bold red]Error:[/] file or directory not found: {Markup.Escape(p)}");
            return 1;
        }

        if (invalidDumpFiles.Count > 0)
        {
            foreach (var p in invalidDumpFiles)
                AnsiConsole.MarkupLine($"[bold red]Error:[/] not a dump file (.dmp/.mdmp): {Markup.Escape(p)}");
            return 1;
        }

        if (dumpPaths.Count < 2)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] at least 2 dump files are required for trend analysis.");
            return 1;
        }

        dumpPaths = [.. dumpPaths.OrderBy(File.GetLastWriteTime)];

        AnsiConsole.MarkupLine($"[bold]Trend analysis:[/] {dumpPaths.Count} dump(s)  [[{(full ? "full" : "lightweight")} mode]]");
        AnsiConsole.WriteLine();

        var snapshots = new List<DumpSnapshot>();
        ReportDoc?[]? capturedSubReports = full ? new ReportDoc?[dumpPaths.Count] : null;

        for (int i = 0; i < dumpPaths.Count; i++)
        {
            var label    = $"D{i + 1}";
            var path     = dumpPaths[i];
            var dispName = ShortName(path);
            DumpSnapshot? snap = null;

            ToolMemoryDiagnostic.Start();

            {
                using var dumpCtx = DumpContext.Open(path);

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue"))
                    .Start($"[bold]{label}[/]  {Markup.Escape(dispName)}  opening...", spinCtx =>
                    {
                        void Upd(string msg) =>
                            spinCtx.Status($"[bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  {Markup.Escape(msg)}");
                        snap = full
                            ? DumpCollector.CollectFull(dumpCtx, Upd)
                            : DumpCollector.CollectLightweight(dumpCtx, Upd);
                    });

                snapshots.Add(snap!);
                var sc = TrendAnalysisReport.ScoreColor(snap!.HealthScore);
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/]  [bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  " +
                    $"[{sc}]{snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}[/]");

                if (capturedSubReports is not null)
                {
                    CommandBase.SuppressVerbose = true;
                    try
                    {
                        var cap = new CaptureSink();
                        cap.Header(
                            $"Per-Dump Report: {label}  —  {Path.GetFileName(path)}",
                            $"{snap.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {snap.ClrVersion ?? "unknown"}  |  Score: {snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}");
                        AnalyzeReport.RenderReport(snap, cap, includeHeader: false);
                        AnalyzeReport.RenderEmbeddedReports(dumpCtx, cap);
                        capturedSubReports[i] = cap.GetDoc();
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"  [yellow]⚠ Sub-reports partial: {Markup.Escape(ex.Message)}[/]");
                        var capErr = new CaptureSink();
                        capErr.Alert(AlertLevel.Warning,
                            $"Sub-reports incomplete for {dispName}: {ex.Message}");
                        capturedSubReports[i] = capErr.GetDoc();
                    }
                    finally
                    {
                        CommandBase.SuppressVerbose = false;
                    }
                }
            }

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        AnsiConsole.WriteLine();

        int baselineIndex = baselineArg - 1;
        if (baselineIndex >= dumpPaths.Count)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]Error:[/] --baseline {baselineArg} is out of range (only {dumpPaths.Count} dump(s) loaded).");
            return 1;
        }

        // .json output = save raw snapshots
        if (a.OutputPath is not null && a.OutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            TrendRawSerializer.Save(snapshots, a.OutputPath, capturedSubReports);
            AnsiConsole.MarkupLine($"[dim]→ Raw snapshot data written to:[/] {Markup.Escape(a.OutputPath)}");
            AnsiConsole.MarkupLine("[dim]Use 'trend-render' to convert to HTML/Markdown/text at any time.[/]");
            return 0;
        }

        using var sink = SinkFactory.Create(a.OutputPath);
        TrendAnalysisReport.RenderTrend(snapshots, sink, ignoreEvents, baselineIndex);

        if (capturedSubReports is not null)
        {
            AnsiConsole.MarkupLine("[bold]Rendering per-dump detailed reports…[/]");
            for (int i = 0; i < dumpPaths.Count; i++)
            {
                var label = $"D{i + 1}";
                var snap  = snapshots[i];
                var sc    = TrendAnalysisReport.ScoreColor(snap.HealthScore);
                AnsiConsole.MarkupLine(
                    $"  [bold]{label}[/]  [dim]{Markup.Escape(ShortName(dumpPaths[i]))}[/]  " +
                    $"[{sc}]{snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}[/]");

                if (capturedSubReports[i] is { } doc)
                    ReportDocReplay.Replay(doc, sink);
            }
        }

        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "trend-analysis requires multiple dump files — use Run() entry point.");


    // ── CLI-only helpers ──────────────────────────────────────────────────────

    private static List<string> ExpandDumpInputs(
        IEnumerable<string> inputs,
        out List<string> missingPaths,
        out List<string> invalidDumpFiles)
    {
        var dumps = new List<string>();
        missingPaths = [];
        invalidDumpFiles = [];

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                dumps.AddRange(Directory.EnumerateFiles(input, "*.dmp",  SearchOption.AllDirectories));
                dumps.AddRange(Directory.EnumerateFiles(input, "*.mdmp", SearchOption.AllDirectories));
                continue;
            }
            if (File.Exists(input))
            {
                if (IsDumpFile(input)) dumps.Add(input);
                else invalidDumpFiles.Add(input);
                continue;
            }
            missingPaths.Add(input);
        }

        return [.. dumps.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsDumpFile(string path) =>
        path.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase);

    private static string ShortName(string path, int max = 42)
    {
        var name = Path.GetFileName(path);
        if (name.Length <= max) return name;
        var ext      = Path.GetExtension(name);
        int keepStem = Math.Max(1, max - ext.Length - 1);
        return name[..keepStem] + "\u2026" + ext;
    }
}
