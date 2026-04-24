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
          --prefix <p>          Prefix used for dump labels (default: D → labels are D1, D2, D3).
                                 E.g. --prefix W1 → W11, W12, W13.
          --str-top <n>          string-duplicates: max groups shown (default 100)
          --str-min-count <n>    string-duplicates: min duplicate count (default 2)
          --str-min-waste <bytes> string-duplicates: min wasted bytes (default 0)
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
        string dumpPrefix = a.GetString("prefix", "D");
        int strTop       = a.GetInt("str-top",       100);
        int strMinCnt    = a.GetInt("str-min-count",   2);
        long strMinWaste = a.GetInt("str-min-waste",   0);
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

        var log = new ProgressLogger();
        log.SectionHeader($"DumpDetective Trend Analysis  {DumpDetective.Core.Utilities.AppInfo.Version}");
        log.Info($"{dumpPaths.Count} dump(s) found  [{(full ? "full" : "lightweight")} mode]");
        log.Blank();

        var snapshots = new List<DumpSnapshot>();
        ReportDoc?[]? capturedSubReports = full ? new ReportDoc?[dumpPaths.Count] : null;

        for (int i = 0; i < dumpPaths.Count; i++)
        {
            var label    = $"{dumpPrefix}{i + 1}";
            var path     = dumpPaths[i];
            var dispName = ShortName(path);
            DumpSnapshot? snap = null;

            ToolMemoryDiagnostic.Start();

            log.Stage($"Dump {i + 1}/{dumpPaths.Count}: {dispName}");
            log.Info("Loading dump file...", indent: true);

            {
                using var dumpCtx = DumpContext.Open(path);

                var clrVer = dumpCtx.ClrVersion ?? "unknown";
                var archNote = dumpCtx.ArchWarning is not null ? $"  ⚠ {dumpCtx.ArchWarning}" : string.Empty;
                log.Success($"Dump loaded  |  CLR {clrVer}{archNote}", indent: true);

                snap = full
                    ? DumpCollector.CollectFull(dumpCtx, log.OnProgress)
                    : DumpCollector.CollectLightweight(dumpCtx, log.OnProgress);

                snapshots.Add(snap);
                var sc = TrendAnalysisReport.ScoreColor(snap.HealthScore);
                log.SuccessM(
                    $"{Markup.Escape(label)} complete  |  [{sc}]{snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}[/]  |  " +
                    $"{snap.TotalObjectCount:N0} objs",
                    indent: true);

                if (capturedSubReports is not null)
                {
                    try
                    {
                        var cap = new CaptureSink();
                        cap.Header(
                            $"Per-Dump Report: {label}  —  {Path.GetFileName(path)}",
                            $"{snap.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {clrVer}  |  Score: {snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}");
                        AnalyzeReport.RenderReport(snap, cap, includeHeader: false);
                        CommandBase.SetOverride("top",       strTop.ToString());
                        CommandBase.SetOverride("min-count", strMinCnt.ToString());
                        CommandBase.SetOverride("min-waste", strMinWaste.ToString());
                        AnalyzeReport.RenderEmbeddedReports(dumpCtx, cap, log);
                        CommandBase.ClearOverrides();
                        capturedSubReports[i] = cap.GetDoc();
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"Sub-reports partial: {ex.Message}", indent: true);
                        var capErr = new CaptureSink();
                        capErr.Alert(AlertLevel.Warning,
                            $"Sub-reports incomplete for {dispName}: {ex.Message}");
                        capturedSubReports[i] = capErr.GetDoc();
                    }
                }
            }

            log.Blank();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        int baselineIndex = baselineArg - 1;
        if (baselineIndex >= dumpPaths.Count)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]Error:[/] --baseline {baselineArg} is out of range (only {dumpPaths.Count} dump(s) loaded).");
            return 1;
        }

        // Build effective output list; split raw-save (.json/.bin) from render paths.
        var explicitOutputs = a.OutputPaths;
        List<string> allOutputs;
        if (explicitOutputs.Count > 0)
        {
            allOutputs = [.. explicitOutputs];
        }
        else
        {
            string dir = dumpPaths.Count > 0
                ? (Path.GetDirectoryName(dumpPaths[0]) ?? ".")
                : ".";
            // Honour every --format value; default to html when none given.
            var formats = a.GetAll("format");
            if (formats.Count > 0)
                allOutputs = [.. formats.Select(f => Path.Combine(dir, $"trend-analysis.{f}"))];
            else
                allOutputs = [Path.Combine(dir, "trend-analysis.html")];
        }

        static bool IsRawPath(string p) =>
            p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".bin",  StringComparison.OrdinalIgnoreCase);

        var rawPaths    = allOutputs.Where(IsRawPath).ToList();
        var renderPaths = allOutputs.Where(p => !IsRawPath(p)).ToList();

        foreach (var rawPath in rawPaths)
            TrendRawSerializer.Save(snapshots, rawPath, capturedSubReports, dumpPrefix);

        if (renderPaths.Count == 0)
        {
            log.Blank();
            foreach (var p in rawPaths)
                log.Success($"Written to: {p}");
            if (rawPaths.Count > 0)
                log.Info("Use 'trend-render' to convert .bin snapshots to HTML/Markdown/text at any time.");
            return 0;
        }

        log.SectionHeader("Rendering Output");
        log.Info("Building trend report...");
        using var sink = SinkFactory.CreateMulti(renderPaths);
        TrendAnalysisReport.RenderTrend(snapshots, sink, ignoreEvents, baselineIndex, dumpPrefix);
        log.Check("Trend report rendered.");

        if (capturedSubReports is not null)
        {
            log.Info($"Rendering {dumpPaths.Count} per-dump detailed report(s)...");
            for (int i = 0; i < dumpPaths.Count; i++)
            {
                var label = $"{dumpPrefix}{i + 1}";
                var snap  = snapshots[i];
                var sc    = TrendAnalysisReport.ScoreColor(snap.HealthScore);
                log.InfoM(
                    $"{Markup.Escape(label)}  {Markup.Escape(ShortName(dumpPaths[i]))}  " +
                    $"[{sc}]{snap.HealthScore}/100  {TrendAnalysisReport.ScoreLabel(snap.HealthScore)}[/]",
                    indent: true);

                if (capturedSubReports[i] is { } doc)
                    ReportDocReplay.Replay(doc, sink);
            }
            log.Check("All per-dump reports rendered.");
        }

        if (sink.IsFile)
        {
            log.Blank();
            foreach (var p in allOutputs.Where(p => !p.Equals("console", StringComparison.OrdinalIgnoreCase)))
                log.Success($"Written to: {p}");
        }
        if (rawPaths.Count > 0)
            log.Info("Use 'trend-render' to convert .bin snapshots to HTML/Markdown/text at any time.");
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
