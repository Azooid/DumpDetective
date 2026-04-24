using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DumpDetective.Analysis;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Json;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Reports;
using DumpDetective.Reporting.Sinks;
using Spectre.Console;

namespace DumpDetective.Commands;

/// <summary>
/// Compares two saved report files (.json or .bin) produced by any DumpDetective command
/// with <c>-o *.json</c> or <c>-o *.bin</c>, and produces a diff report.
///
/// Workflow:
///   1. Run analysis twice:  DumpDetective analyze before.dmp -o before.bin
///                           DumpDetective analyze after.dmp  -o after.bin
///   2. Diff them:           DumpDetective diff before.bin after.bin -o delta.html
/// </summary>
public sealed class DiffCommand : ICommand
{
    public string Name               => "diff";
    public string Description        => "Compare two saved report files (.json/.bin) and produce a diff report.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective diff <before.json|before.bin> <after.json|after.bin> [options]

        Compares two saved report files and produces a diff report highlighting what changed.
        No dump file required — all data comes from the saved files.

        Supported input formats
        ────────────────────────
          report     Produced by any single-dump command with -o *.json or -o *.bin.
                       DumpDetective analyze before.dmp --full -o before.bin
                       DumpDetective analyze after.dmp  --full -o after.bin
                       DumpDetective diff before.bin after.bin -o delta.html

          trend-raw  Produced by trend-analysis -o *.json or -o *.bin.
                     Diffs the full rendered trend summary (health scores, heap sizes,
                     thread counts, etc.) between the two trend runs.
                       DumpDetective trend-analysis *.dmp -o week1.bin
                       DumpDetective trend-analysis *.dmp -o week2.bin
                       DumpDetective diff week1.bin week2.bin -o trend-delta.html

                     Use --command to diff only a specific per-dump sub-report chapter
                     (requires both files to have been saved with --full):
                       DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html

        What is diffed
        ───────────────
          Tables       Row keys matched by the key column (default: column 0).
                       Changed cells shown as "new ← old".
          Alerts       Matched by title. Level and detail changes highlighted.
          Key-Values   Matched by key. Changed values shown as "new ← old".
          Details      Accordion blocks included from the "after" file as-is.

        Options:
          --key-col <n>        Column index (0-based) used as the row key for tables (default: 0)
          --changed-only       Omit chapters/sections with no changes
          --show-same          Include unchanged rows in diff tables (default: omitted)
          --command <name>     For trend-raw: diff only this command's embedded sub-report chapters.
                               Repeatable (e.g. --command memory-leak --command heap-stats).
                               Dumps are matched by filename; unmatched dumps are listed as Added/Removed.
          --ignore-event <t>   For trend-raw: exclude event publisher types containing <t> when
                               rendering trend summaries. Repeatable.
          -o, --output <file>  Output path (.html / .md / .txt / .json / .bin)
                               Default: <before>-vs-<after>.html
          -h, --help           Show this help

        Examples:
          DumpDetective diff before.bin after.bin
          DumpDetective diff before.json after.json -o diff-report.html
          DumpDetective diff week1.bin week2.bin --changed-only -o delta.html
          DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html
          DumpDetective diff week1.bin week2.bin --command memory-leak --command heap-stats -o subset.html
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (a.Help || args.Length == 0) { AnsiConsole.Write(new Markup(Help + "\n")); return 0; }

        var positionals = a.Positionals;
        if (positionals.Count < 2)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Two report files are required: [dim]<before> <after>[/]");
            return 1;
        }

        string beforePath = positionals[0];
        string afterPath  = positionals[1];

        if (!File.Exists(beforePath)) { AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: {Markup.Escape(beforePath)}"); return 1; }
        if (!File.Exists(afterPath))  { AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: {Markup.Escape(afterPath)}");  return 1; }

        int     keyCol       = a.GetInt("key-col", 0);
        bool    changedOnly  = a.HasFlag("changed-only");
        bool    showSame     = a.HasFlag("show-same");
        var     commands     = a.GetAll("command").ToList();
        var     ignoreEvents = a.GetAll("ignore-event").ToList();
        string? outputPath   = a.OutputPath ?? BuildDefaultOutput(beforePath, afterPath);

        string? beforeJson, afterJson;
        try   { beforeJson = ReadRaw(beforePath); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]✗[/] Could not read before file: {Markup.Escape(ex.Message)}"); return 1; }
        try   { afterJson  = ReadRaw(afterPath); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]✗[/] Could not read after file: {Markup.Escape(ex.Message)}");  return 1; }

        string? beforeFormat = PeekFormat(beforeJson);
        string? afterFormat  = PeekFormat(afterJson);

        if (beforeFormat != afterFormat)
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File formats do not match: before is [bold]{beforeFormat ?? "unknown"}[/], after is [bold]{afterFormat ?? "unknown"}[/].");
            return 1;
        }

        var diffOpts = new ReportDiffer.Options { KeyCol = keyCol, ChangedOnly = changedOnly, ShowSame = showSame };

        // ── trend-raw diff ────────────────────────────────────────────────────
        if (beforeFormat == "trend-raw")
            return RunTrendDiff(beforePath, afterPath, beforeJson, afterJson,
                                commands, ignoreEvents, outputPath, diffOpts);

        // ── single-dump report diff ───────────────────────────────────────────
        if (beforeFormat == "report")
            return RunReportDiff(beforePath, afterPath, beforeJson, afterJson,
                                 outputPath, diffOpts);

        AnsiConsole.MarkupLine($"[bold red]✗[/] Unrecognised file format: [bold]{beforeFormat ?? "unknown"}[/]. Expected [dim]report[/] or [dim]trend-raw[/].");
        return 1;
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => sink.Alert(AlertLevel.Warning,
            "diff cannot be used in embedded mode",
            "Run as a standalone command: DumpDetective diff <before> <after>");

    // ── Single-dump report diff ───────────────────────────────────────────────

    private static int RunReportDiff(
        string beforePath, string afterPath,
        string beforeJson, string afterJson,
        string? outputPath, ReportDiffer.Options opts)
    {
        DumpReportEnvelope? beforeEnv, afterEnv;
        try   { beforeEnv = JsonSerializer.Deserialize(beforeJson, CoreJsonContext.Default.DumpReportEnvelope); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]✗[/] before file: {Markup.Escape(ex.Message)}"); return 1; }
        try   { afterEnv  = JsonSerializer.Deserialize(afterJson,  CoreJsonContext.Default.DumpReportEnvelope); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]✗[/] after file: {Markup.Escape(ex.Message)}");  return 1; }

        if (beforeEnv?.Doc is null) { AnsiConsole.MarkupLine("[bold red]✗[/] before file: invalid report (missing doc)."); return 1; }
        if (afterEnv?.Doc is null)  { AnsiConsole.MarkupLine("[bold red]✗[/] after file: invalid report (missing doc).");  return 1; }

        string beforeLabel = LabelFor(beforePath, beforeEnv.GeneratedAt);
        string afterLabel  = LabelFor(afterPath,  afterEnv.GeneratedAt);
        AnsiConsole.MarkupLine($"Comparing [bold]{Markup.Escape(beforeLabel)}[/] → [bold]{Markup.Escape(afterLabel)}[/]");

        var diffDoc = ReportDiffer.Diff(beforeEnv.Doc, afterEnv.Doc, beforeLabel, afterLabel, opts);
        using var sink = SinkFactory.Create(outputPath);
        ReportDocReplay.Replay(diffDoc, sink);
        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    // ── Trend-raw diff ────────────────────────────────────────────────────────

    private static int RunTrendDiff(
        string beforePath, string afterPath,
        string beforeJson, string afterJson,
        List<string> commands, List<string> ignoreEvents,
        string? outputPath, ReportDiffer.Options opts)
    {
        List<DumpSnapshot> beforeSnaps, afterSnaps;
        string beforePrefix, afterPrefix;
        try   { (beforeSnaps, beforePrefix) = TrendRawSerializer.Load(beforePath); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]\u2717[/] before file: {Markup.Escape(ex.Message)}"); return 1; }
        try   { (afterSnaps,  afterPrefix)  = TrendRawSerializer.Load(afterPath); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]✗[/] after file: {Markup.Escape(ex.Message)}");  return 1; }

        string beforeLabel = $"{Path.GetFileName(beforePath)} ({beforeSnaps.Count} dumps)";
        string afterLabel  = $"{Path.GetFileName(afterPath)} ({afterSnaps.Count} dumps)";

        AnsiConsole.MarkupLine($"Comparing trend [bold]{Markup.Escape(beforeLabel)}[/] → [bold]{Markup.Escape(afterLabel)}[/]");

        ReportDoc beforeDoc, afterDoc;

        if (commands.Count > 0)
        {
            // --command mode: diff the per-dump sub-reports for matching dump filenames
            return RunTrendSubReportDiff(
                beforeSnaps, afterSnaps, beforeLabel, afterLabel,
                commands, outputPath, opts);
        }

        // Default: render both trend summaries into ReportDocs, then diff them
        beforeDoc = RenderTrendToDoc(beforeSnaps, ignoreEvents, beforePrefix);
        afterDoc  = RenderTrendToDoc(afterSnaps,  ignoreEvents, afterPrefix);

        var diffDoc = ReportDiffer.Diff(beforeDoc, afterDoc, beforeLabel, afterLabel, opts);
        using var sink = SinkFactory.Create(outputPath);
        ReportDocReplay.Replay(diffDoc, sink);
        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    /// <summary>
    /// Diffs per-dump sub-reports between two trend files, matching dumps by filename.
    /// Dumps present in only one file are listed as Added/Removed.
    /// </summary>
    private static int RunTrendSubReportDiff(
        List<DumpSnapshot> beforeSnaps, List<DumpSnapshot> afterSnaps,
        string beforeLabel, string afterLabel,
        List<string> commands, string? outputPath,
        ReportDiffer.Options opts)
    {
        bool beforeHasSubs = beforeSnaps.Any(s => s.SubReport is not null);
        bool afterHasSubs  = afterSnaps .Any(s => s.SubReport is not null);

        if (!beforeHasSubs || !afterHasSubs)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --command requires both files to have embedded sub-reports.");
            AnsiConsole.MarkupLine("[dim]  Re-run trend-analysis with --full: trend-analysis *.dmp --full -o file.bin[/]");
            return 1;
        }

        // Index snapshots by dump filename (without extension)
        var beforeIdx = beforeSnaps.ToDictionary(
            s => Path.GetFileNameWithoutExtension(s.DumpPath),
            s => s,
            StringComparer.OrdinalIgnoreCase);
        var afterIdx = afterSnaps.ToDictionary(
            s => Path.GetFileNameWithoutExtension(s.DumpPath),
            s => s,
            StringComparer.OrdinalIgnoreCase);

        var allKeys = afterIdx.Keys
            .Concat(beforeIdx.Keys.Where(k => !afterIdx.ContainsKey(k)))
            .ToList();

        var combinedDoc = new ReportDoc();

        // Summary chapter
        int matched  = allKeys.Count(k => beforeIdx.ContainsKey(k) && afterIdx.ContainsKey(k));
        int addedCnt = allKeys.Count(k => !beforeIdx.ContainsKey(k));
        int removedCnt = allKeys.Count(k => !afterIdx.ContainsKey(k));
        combinedDoc.Chapters.Add(new ReportChapter
        {
            Title    = "Trend Sub-Report Diff Summary",
            Subtitle = $"{beforeLabel} → {afterLabel}  |  commands: {string.Join(", ", commands)}",
            Sections =
            [
                new ReportSection
                {
                    Title = "Overview",
                    Elements =
                    [
                        new ReportKeyValues { Pairs =
                        [
                            new ReportPair("Before",           beforeLabel),
                            new ReportPair("After",            afterLabel),
                            new ReportPair("Commands diffed",  string.Join(", ", commands)),
                            new ReportPair("Dumps matched",    matched.ToString()),
                            new ReportPair("Dumps added",      addedCnt.ToString()),
                            new ReportPair("Dumps removed",    removedCnt.ToString()),
                        ]},
                    ],
                }
            ]
        });

        foreach (var key in allKeys)
        {
            beforeIdx.TryGetValue(key, out var bs);
            afterIdx .TryGetValue(key, out var ас);

            string dumpLabel = key;

            if (bs is null && ас is not null)
            {
                // Only in after — include as-is
                var doc = SliceIfNeeded(ас.SubReport, commands);
                if (doc is not null)
                    foreach (var ch in doc.Chapters)
                        combinedDoc.Chapters.Add(PrefixTitle(ch, $"[Added] {dumpLabel}:"));
                continue;
            }
            if (ас is null && bs is not null)
            {
                if (!opts.ChangedOnly)
                {
                    var doc = SliceIfNeeded(bs.SubReport, commands);
                    if (doc is not null)
                        foreach (var ch in doc.Chapters)
                            combinedDoc.Chapters.Add(PrefixTitle(ch, $"[Removed] {dumpLabel}:"));
                }
                continue;
            }

            if (bs is not null && ас is not null)
            {
                var beforeDoc = SliceIfNeeded(bs.SubReport, commands);
                var afterDoc  = SliceIfNeeded(ас.SubReport, commands);

                if (beforeDoc is null || afterDoc is null) continue;

                string bLabel = $"{dumpLabel} (before, score {bs.HealthScore}/100)";
                string aLabel = $"{dumpLabel} (after,  score {ас.HealthScore}/100)";
                AnsiConsole.MarkupLine($"  Diffing dump [bold]{Markup.Escape(key)}[/]  {bs.HealthScore} → {ас.HealthScore}");

                var dumpDiff = ReportDiffer.Diff(beforeDoc, afterDoc, bLabel, aLabel, opts);
                foreach (var ch in dumpDiff.Chapters)
                    combinedDoc.Chapters.Add(ch);
            }
        }

        using var sink = SinkFactory.Create(outputPath);
        ReportDocReplay.Replay(combinedDoc, sink);
        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ReportDoc RenderTrendToDoc(
        List<DumpSnapshot> snaps,
        IReadOnlyList<string> ignoreEvents,
        string dumpPrefix = "D")
    {
        var cap = new CaptureSink();
        TrendAnalysisReport.RenderTrend(snaps, cap, ignoreEvents, dumpPrefix: dumpPrefix);
        return cap.GetDoc();
    }

    private static ReportDoc? SliceIfNeeded(ReportDoc? doc, List<string> commands)
    {
        if (doc is null) return null;
        if (commands.Count == 0) return doc;
        var sliced = ReportDocSlicer.Slice(doc, commands);
        return sliced.Chapters.Count > 0 ? sliced : null;
    }

    private static ReportChapter PrefixTitle(ReportChapter ch, string prefix) => new()
    {
        Title       = $"{prefix} {ch.Title}",
        Subtitle    = ch.Subtitle,
        CommandName = ch.CommandName,
        NavLevel    = ch.NavLevel,
        Sections    = ch.Sections,
    };

    private static string? PeekFormat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("format", out var fp) ? fp.GetString() : null;
        }
        catch { return null; }
    }

    private static string ReadRaw(string path)
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

    private static string BuildDefaultOutput(string before, string after)
    {
        string b = Path.GetFileNameWithoutExtension(before);
        string a = Path.GetFileNameWithoutExtension(after);
        return $"{b}-vs-{a}.html";
    }

    private static string LabelFor(string path, string generatedAt)
    {
        string name = Path.GetFileName(path);
        if (DateTime.TryParse(generatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return $"{name} ({dt.ToLocalTime():yyyy-MM-dd HH:mm})";
        return name;
    }
}
