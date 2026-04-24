using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DumpDetective.Analysis;
using DumpDetective.Core.Json;
using DumpDetective.Reporting;

namespace DumpDetective.Commands;

/// <summary>
/// Converts a DumpDetective JSON file to any output format without re-analyzing.
/// Handles both "trend-raw" (from trend-analysis --output *.json) and "report" formats.
/// </summary>
public sealed class TrendRenderCommand : ICommand
{
    public string Name               => "trend-render";
    public string Description        => "Convert a trend-raw JSON file to HTML/Markdown/text without re-analyzing.";
    public bool   IncludeInFullAnalyze => false; // replay only

    private const string Help = """
        Usage: DumpDetective render      <data.json|data.bin> [options]
               DumpDetective trend-render <data.json|data.bin> [options]  (alias)

        Converts a DumpDetective JSON or BIN file to any output format without re-analyzing.
        No dump files are required — all data comes from the file.

        Accepted input formats
        ──────────────────────
          .json  Plain JSON report or trend-raw file.
          .bin   Brotli-compressed JSON (produced by '--output *.bin').
                 Same structure as .json — automatically decompressed.

        Accepted report types
        ─────────────────────
          trend-raw  Produced by 'trend-analysis --output *.json'.
                     Contains raw snapshot metrics for all dumps and, when the
                     original run used --full, complete per-dump sub-reports.
          report     Produced by any single-dump command with '--output *.json'
                     or '--output *.bin' (e.g. 'analyze dump.dmp --output report.bin').
                     The report is replayed as-is; --baseline has no effect.

        Options:
          --baseline <n>         1-based index of the dump to use as the trend
                                 baseline (trend-raw only; default: 1 = first dump).
          --ignore-event <type>  Exclude publisher types containing <type> from
                                 the Event Leak table (trend-raw only). Repeatable.
          --mini                 Render trend summary only — suppress per-dump
                                 sub-reports even when they are present in the JSON.
          --from <n>             Extract dump #N's full sub-report as a standalone
                                 file. Requires the JSON to have been saved with
                                 --full. 1-based index.
          --command <name>       Extract only the named command's chapter(s).
                                 Combine with --from to target a single dump.
                                 Repeatable (e.g. --command memory-leak --command heap-stats).
                                 Valid names: any command that runs in --full analyze
                                 (heap-stats, memory-leak, high-refs, …).
          -o, --output <file>    Write report to file (.html / .md / .txt / .json / .bin)
                                 Use '--output console' to print to the terminal.
                                 Omit for default: <inputfile>.html
          -h, --help             Show this help

        Examples:
          DumpDetective render snapshots.json
          DumpDetective render snapshots.json --output report.html
          DumpDetective render snapshots.json --output console
          DumpDetective render snapshots.json --mini --output trend-only.html
          DumpDetective render snapshots.json --baseline 2 --output report.html
          DumpDetective render snapshots.json --from 4 --output d4-full.html
          DumpDetective render snapshots.json --from 4 --command memory-leak --output d4-memleak.html
          DumpDetective render snapshots.json --command memory-leak --output all-memleak.html
          DumpDetective render snapshots.json --command memory-leak --command heap-stats --output d4-subset.html
          DumpDetective render analyze-report.json
          DumpDetective render analyze-report.bin --output report.html
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (a.Help || args.Length == 0)
        {
            AnsiConsole.Write(new Markup(Help));
            return 0;
        }

        string? rawFile      = a.Positionals.FirstOrDefault();
        int     baselineArg  = a.GetInt("baseline", 1);
        var     ignoreEvents = a.GetAll("ignore-event").ToList();
        bool    mini         = a.HasFlag("mini");
        int     fromArg      = a.GetInt("from", 0);
        var     commands     = a.GetAll("command").ToList();

        // Default output is HTML; "--output console" or "--format console" explicitly requests console output.
        // "--format md" (etc.) without "--output" overrides the extension of the auto-generated path.
        string? outputPath;
        if (a.OutputPath is not null)
        {
            outputPath = a.OutputPath; // explicit --output wins
        }
        else if (a.Format is not null && !a.Format.Equals("console", StringComparison.OrdinalIgnoreCase) && rawFile is not null)
        {
            outputPath = Path.ChangeExtension(rawFile, a.Format);
        }
        else if (a.Format?.Equals("console", StringComparison.OrdinalIgnoreCase) == true)
        {
            outputPath = null; // explicit console
        }
        else
        {
            outputPath = rawFile is not null ? Path.ChangeExtension(rawFile, ".html") : null;
        }

        if (baselineArg < 1)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] --baseline must be a positive integer.");
            return 1;
        }

        if (fromArg < 0)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] --from must be a positive integer.");
            return 1;
        }

        if (rawFile is null)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] JSON file path is required.");
            return 1;
        }

        if (!File.Exists(rawFile))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] file not found: {Markup.Escape(rawFile)}");
            return 1;
        }

        string json;
        try { json = ReadJson(rawFile); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]Error reading file:[/] {Markup.Escape(ex.Message)}"); return 1; }

        string? format = null;
        try
        {
            using var jdoc = JsonDocument.Parse(json);
            if (jdoc.RootElement.TryGetProperty("format", out var fp))
                format = fp.GetString();
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] file is not valid JSON: {Markup.Escape(ex.Message)}");
            return 1;
        }

        // ── Plain report JSON ─────────────────────────────────────────────────
        if (format == "report")
        {
            if (fromArg > 0 || commands.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold red]Error:[/] --from and --command require a trend-raw JSON file " +
                    "(produced by: trend-analysis ... --full --output snapshots.json).");
                return 1;
            }

            DumpReportEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize(json, CoreJsonContext.Default.DumpReportEnvelope); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]Error reading report envelope:[/] {Markup.Escape(ex.Message)}"); return 1; }

            if (envelope?.Doc is null)
            {
                AnsiConsole.MarkupLine("[bold red]Error:[/] invalid report JSON (missing 'doc' field).");
                return 1;
            }

            AnsiConsole.MarkupLine($"Rendering report from {Markup.Escape(Path.GetFileName(rawFile))}");
            using var sink2 = SinkFactory.Create(outputPath);
            ReportDocReplay.Replay(envelope.Doc, sink2);
            if (sink2.IsFile && sink2.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink2.FilePath)}");
            return 0;
        }

        // ── Trend-raw JSON ────────────────────────────────────────────────────
        List<DumpSnapshot> snapshots;
        string dumpPrefix;
        try { (snapshots, dumpPrefix) = TrendRawSerializer.Load(rawFile); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]Error reading raw trend data:[/] {Markup.Escape(ex.Message)}"); return 1; }

        if (snapshots.Count < 2)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] raw data contains fewer than 2 snapshots — nothing to trend.");
            return 1;
        }

        // ── --from / --command: sub-report extraction mode ────────────────────
        if (fromArg > 0 || commands.Count > 0)
        {
            // Validate --from range
            if (fromArg > 0 && fromArg > snapshots.Count)
            {
                AnsiConsole.MarkupLine(
                    $"[bold red]Error:[/] --from {fromArg} is out of range (only {snapshots.Count} snapshot(s) in file).");
                return 1;
            }

            // Determine which snapshots to extract from
            var targets = fromArg > 0
                ? [snapshots[fromArg - 1]]
                : (IReadOnlyList<DumpSnapshot>)snapshots;

            // Verify sub-reports are present on all targets
            var missing = targets
                .Select((s, i) => (s, idx: fromArg > 0 ? fromArg : i + 1))
                .Where(t => t.s.SubReport is null)
                .ToList();

            if (missing.Count > 0)
            {
                foreach (var (s, idx) in missing)
                    AnsiConsole.MarkupLine(
                        $"[bold red]Error:[/] Dump #{idx} ({Markup.Escape(Path.GetFileName(s.DumpPath))}) " +
                        $"has no embedded sub-report.\n" +
                        $"  Re-run with --full: [dim]trend-analysis ... --full --output {Markup.Escape(Path.GetFileName(rawFile))}[/]");
                return 1;
            }

            using var sink = SinkFactory.Create(outputPath);

            for (int i = 0; i < targets.Count; i++)
            {
                var snap  = targets[i];
                var dIdx  = fromArg > 0 ? fromArg : i + 1;
                var doc   = snap.SubReport!;

                // Slice to requested commands if --command was given
                if (commands.Count > 0)
                {
                    doc = ReportDocSlicer.Slice(doc, commands);
                    if (doc.Chapters.Count == 0)
                    {
                        var available = string.Join(", ", ReportDocSlicer.AvailableCommands(snap.SubReport!));
                        AnsiConsole.MarkupLine(
                            $"[bold red]Error:[/] No chapters matched for D{dIdx}. " +
                            $"Available commands: [dim]{Markup.Escape(available)}[/]");
                        return 1;
                    }
                }

                AnsiConsole.MarkupLine(
                    $"Extracting D{dIdx} ({Markup.Escape(Path.GetFileName(snap.DumpPath))}, " +
                    $"score {snap.HealthScore}/100)" +
                    (commands.Count > 0 ? $"  commands: {Markup.Escape(string.Join(", ", commands))}" : string.Empty));

                ReportDocReplay.Replay(doc, sink);
            }

            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            return 0;
        }

        // ── Standard trend render ─────────────────────────────────────────────
        int baselineIndex = baselineArg - 1;
        if (baselineIndex >= snapshots.Count)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]Error:[/] --baseline {baselineArg} is out of range (only {snapshots.Count} snapshot(s) in file).");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Rendering trend report from {Markup.Escape(Path.GetFileName(rawFile))}  " +
            $"({snapshots.Count} snapshots)  baseline: {dumpPrefix}{baselineArg}" +
            (mini ? "  [dim](--mini: sub-reports suppressed)[/]" : string.Empty));

        using var trendSink = SinkFactory.Create(outputPath);
        TrendAnalysisReport.RenderTrend(snapshots, trendSink, ignoreEvents, baselineIndex, dumpPrefix);

        if (!mini)
        {
            bool hasSubReports = snapshots.Any(s => s.SubReport is not null);
            if (hasSubReports)
            {
                AnsiConsole.MarkupLine("Rendering per-dump detailed sub-reports…");
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var snap = snapshots[i];
                    if (snap.SubReport is null) continue;
                    AnsiConsole.MarkupLine($"  {dumpPrefix}{i + 1}  {Markup.Escape(Path.GetFileName(snap.DumpPath))}  {snap.HealthScore}/100");
                    ReportDocReplay.Replay(snap.SubReport, trendSink);
                }
            }
        }

        if (trendSink.IsFile && trendSink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(trendSink.FilePath)}");
        return 0;
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "trend-render operates on JSON files, not dump files — use Run() entry point.");

    /// <summary>
    /// Reads the file as UTF-8 JSON, decompressing Brotli first for .bin files.
    /// </summary>
    private static string ReadJson(string path)
    {
        if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }

}
