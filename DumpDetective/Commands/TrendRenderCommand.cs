using DumpDetective.Helpers;
using DumpDetective.Models;
using DumpDetective.Output;
using System.Text.Json;
using System.Text;

namespace DumpDetective.Commands;

internal static class TrendRenderCommand
{
    private const string Help = """
        Usage: DumpDetective trend-render <data.json> [options]
               DumpDetective render        <data.json> [options]

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
          -o, --output <file>    Write report to file  (.html / .md / .txt / .json)
                                 Omit for console output.
          -h, --help             Show this help

        Examples:
          DumpDetective trend-render snapshots.json --output report.html
          DumpDetective trend-render snapshots.json --baseline 2 --output report.html
          DumpDetective render       analyze-report.json --output report.html
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        string? rawFile      = null;
        string? output       = null;
        int     baselineArg  = 1;
        var     ignoreEvents = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length)
                output = args[++i];
            else if (args[i] == "--baseline" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out baselineArg) || baselineArg < 1)
                {
                    Console.Error.WriteLine("Error: --baseline must be a positive integer (1 = first dump).");
                    return 1;
                }
            }
            else if (args[i] == "--ignore-event" && i + 1 < args.Length)
                ignoreEvents.Add(args[++i]);
            else if (!args[i].StartsWith('-') && rawFile is null)
                rawFile = args[i];
        }

        if (rawFile is null)
        {
            Console.Error.WriteLine("Error: JSON file path is required.");
            Console.Error.WriteLine(Help);
            return 1;
        }

        if (!File.Exists(rawFile))
        {
            Console.Error.WriteLine($"Error: file not found: {rawFile}");
            return 1;
        }

        string json;
        try { json = File.ReadAllText(rawFile, Encoding.UTF8); }
        catch (Exception ex) { Console.Error.WriteLine($"Error reading file: {ex.Message}"); return 1; }

        // Peek at the "format" field to decide how to handle the file
        string? format = null;
        try
        {
            using var jdoc = JsonDocument.Parse(json);
            if (jdoc.RootElement.TryGetProperty("format", out var fp))
                format = fp.GetString();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: file is not valid JSON: {ex.Message}");
            return 1;
        }

        // ── Plain report JSON (from any single command --output *.json) ───────
        if (format == "report")
        {
            DumpReportEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(json, RawTrendContext.Default.DumpReportEnvelope);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading report envelope: {ex.Message}");
                return 1;
            }

            if (envelope?.Doc is null)
            {
                Console.Error.WriteLine("Error: invalid report JSON (missing 'doc' field).");
                return 1;
            }

            Console.WriteLine($"Rendering report from {Path.GetFileName(rawFile)}");
            using var sink = IRenderSink.Create(output);
            ReportDocReplay.Replay(envelope.Doc, sink);
            if (sink.IsFile && sink.FilePath is not null)
                Console.WriteLine($"\n→ Written to: {sink.FilePath}");
            return 0;
        }

        // ── Trend-raw JSON (from trend-analysis --output *.json) ──────────────
        RawTrendExport export;
        try
        {
            export = TrendRawSerializer.Load(rawFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading raw trend data: {ex.Message}");
            return 1;
        }

        if (export.Snapshots.Count < 2)
        {
            Console.Error.WriteLine("Error: raw data contains fewer than 2 snapshots — nothing to trend.");
            return 1;
        }

        var snapshots = export.Snapshots.Select(s => s.ToSnapshot()).ToList();

        int baselineIndex = baselineArg - 1;
        if (baselineIndex >= snapshots.Count)
        {
            Console.Error.WriteLine(
                $"Error: --baseline {baselineArg} is out of range (only {snapshots.Count} snapshot(s) in file).");
            return 1;
        }

        Console.WriteLine($"Rendering trend report from {Path.GetFileName(rawFile)}  " +
                          $"[{snapshots.Count} snapshots]  baseline: D{baselineArg}");

        using var trendSink = IRenderSink.Create(output);
        TrendAnalysisCommand.RenderTrend(snapshots, trendSink, ignoreEvents, baselineIndex);

        // ── Replay per-dump sub-reports if they were captured ─────────────────
        bool hasSubReports = export.Snapshots.Any(s => s.SubReport is not null);
        if (hasSubReports)
        {
            Console.WriteLine("Rendering per-dump detailed sub-reports…");
            for (int i = 0; i < export.Snapshots.Count; i++)
            {
                var sData = export.Snapshots[i];
                if (sData.SubReport is null) continue;

                var snap  = snapshots[i];
                var sc    = snap.HealthScore >= 70 ? "green" : snap.HealthScore >= 40 ? "yellow" : "red";
                Console.WriteLine($"  D{i + 1}  {Path.GetFileName(sData.DumpPath)}  {snap.HealthScore}/100");

                ReportDocReplay.Replay(sData.SubReport, trendSink);
            }
        }

        if (trendSink.IsFile && trendSink.FilePath is not null)
            Console.WriteLine($"\n→ Written to: {trendSink.FilePath}");

        return 0;
    }
}
