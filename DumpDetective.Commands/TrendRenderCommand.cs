using System.Text;
using System.Text.Json;
using DumpDetective.Analysis;
using DumpDetective.Core.Json;

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
        Usage: DumpDetective render      <data.json> [options]
               DumpDetective trend-render <data.json> [options]  (alias)

        Converts a DumpDetective JSON file to any output format without re-analyzing.
        No dump files are required — all data comes from the JSON.

        Accepted JSON formats
        ─────────────────────
          trend-raw  Produced by 'trend-analysis --output *.json'.
                     Contains raw snapshot metrics for all dumps and, when the
                     original run used --full, complete per-dump sub-reports.
          report     Produced by any single-dump command with '--output *.json'
                     (e.g. 'analyze dump.dmp --output report.json').
                     The report is replayed as-is; --baseline has no effect.

        Options:
          --baseline <n>         1-based index of the dump to use as the trend
                                 baseline (trend-raw only; default: 1 = first dump).
          --ignore-event <type>  Exclude publisher types containing <type> from
                                 the Event Leak table (trend-raw only). Repeatable.
          -o, --output <file>    Write report to file (.html / .md / .txt / .json)
                                 Omit for console output.
          -h, --help             Show this help

        Examples:
          DumpDetective trend-render snapshots.json --output report.html
          DumpDetective trend-render snapshots.json --baseline 2 --output report.html
          DumpDetective render       analyze-report.json --output report.html
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

        if (baselineArg < 1)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] --baseline must be a positive integer.");
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
        try { json = File.ReadAllText(rawFile, Encoding.UTF8); }
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
            DumpReportEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize(json, CoreJsonContext.Default.DumpReportEnvelope); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]Error reading report envelope:[/] {Markup.Escape(ex.Message)}"); return 1; }

            if (envelope?.Doc is null)
            {
                AnsiConsole.MarkupLine("[bold red]Error:[/] invalid report JSON (missing 'doc' field).");
                return 1;
            }

            AnsiConsole.MarkupLine($"Rendering report from {Markup.Escape(Path.GetFileName(rawFile))}");
            using var sink2 = SinkFactory.Create(a.OutputPath);
            ReportDocReplay.Replay(envelope.Doc, sink2);
            if (sink2.IsFile && sink2.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink2.FilePath)}");
            return 0;
        }

        // ── Trend-raw JSON ────────────────────────────────────────────────────
        List<DumpSnapshot> snapshots;
        try { snapshots = TrendRawSerializer.Load(rawFile); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]Error reading raw trend data:[/] {Markup.Escape(ex.Message)}"); return 1; }

        if (snapshots.Count < 2)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] raw data contains fewer than 2 snapshots — nothing to trend.");
            return 1;
        }

        int baselineIndex = baselineArg - 1;
        if (baselineIndex >= snapshots.Count)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]Error:[/] --baseline {baselineArg} is out of range (only {snapshots.Count} snapshot(s) in file).");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Rendering trend report from {Markup.Escape(Path.GetFileName(rawFile))}  " +
            $"[{snapshots.Count} snapshots]  baseline: D{baselineArg}");

        using var trendSink = SinkFactory.Create(a.OutputPath);
        TrendAnalysisReport.RenderTrend(snapshots, trendSink, ignoreEvents, baselineIndex);

        bool hasSubReports = snapshots.Any(s => s.SubReport is not null);
        if (hasSubReports)
        {
            AnsiConsole.MarkupLine("Rendering per-dump detailed sub-reports…");
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                if (snap.SubReport is null) continue;
                AnsiConsole.MarkupLine($"  D{i + 1}  {Markup.Escape(Path.GetFileName(snap.DumpPath))}  {snap.HealthScore}/100");
                ReportDocReplay.Replay(snap.SubReport, trendSink);
            }
        }

        if (trendSink.IsFile && trendSink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(trendSink.FilePath)}");
        return 0;
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Warning, "trend-render operates on JSON files, not dump files — use Run() entry point.");

}
