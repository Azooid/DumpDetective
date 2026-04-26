using System.Diagnostics;
using DumpDetective.Analysis;

namespace DumpDetective.Commands;

/// <summary>
/// Scored health report for a single dump.
/// Mini (default): lightweight scored summary — fast.
/// Full (--full):  scored summary + all ICommand.IncludeInFullAnalyze sub-reports.
/// </summary>
public sealed class AnalyzeCommand : ICommand
{
    public string Name               => "analyze";
    public string Description        => "Scored health report for a single dump (use --full for all sub-reports).";
    public bool   IncludeInFullAnalyze => false;  // orchestrator — not nested

    private const string Help = """
        Usage: DumpDetective analyze <dump-file> [options]

        Scored health report for a single dump.

        Report modes
        ─────────────
          (default)  Mini report — lightweight scored summary:
                     memory, threads, exceptions, async backlog, leaks, handles, WCF,
                     connections, modules.  Fast (~heap walk once).

          --full     Full report — scored summary PLUS all individual sub-reports
                     embedded as chapters in one document.  Recommended with --output;
                     significantly slower.

        Options:
          --full                   Full combined report (scored summary + all sub-reports)
          --str-top <n>            string-duplicates: max groups shown (default 100)
          --str-min-count <n>      string-duplicates: min duplicate count (default 2)
          --str-min-waste <bytes>  string-duplicates: min wasted bytes (default 0)
          --bfs-depth <n>          static-refs: BFS sample depth (default: 1% of heap objects)
          --exact                  static-refs: disable sampling, full BFS (slower but precise)
          -o, --output <file>      Write report to file (.html / .md / .txt / .json)
          -h, --help               Show this help

        Examples:
          DumpDetective analyze app.dmp
          DumpDetective analyze app.dmp --full --output full-report.html
          DumpDetective analyze app.dmp --full --str-min-waste 1048576
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a          = CliArgs.Parse(args);
        bool    full       = a.HasFlag("full");
        int     strTop     = a.GetInt("str-top",       100);
        int     strMinCnt  = a.GetInt("str-min-count",   2);
        long    strMinWaste= a.GetInt("str-min-waste",   0);
        bool    bfsExact   = a.HasFlag("exact");
        long?   bfsDepth   = a.GetOption("bfs-depth") is string bd && long.TryParse(bd, out long bdn) ? bdn : null;
        string? dumpPath   = a.DumpPath;
        string? outputPath = a.OutputPath;

        if (dumpPath is null)       { AnsiConsole.MarkupLine("[bold red]✗[/] dump file path required."); return 1; }
        if (!File.Exists(dumpPath)) { AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: {Markup.Escape(dumpPath)}"); return 1; }

        var log = new ProgressLogger();
        log.SectionHeader($"DumpDetective Analysis  {DumpDetective.Core.Utilities.AppInfo.Version}");
        log.Info($"Analyzing dump: {Path.GetFileName(dumpPath)}");
        log.Info("Loading dump file...", indent: true);

        try
        {
            using var dumpCtx = DumpContext.Open(dumpPath);
            var clrVer = dumpCtx.ClrVersion ?? "unknown";
            log.Success($"Dump loaded  |  CLR {clrVer}", indent: true);
            if (dumpCtx.ArchWarning is not null)
                log.Warn(dumpCtx.ArchWarning, indent: true);

            log.Blank();
            log.SectionHeader("Collection");

            var collSw = Stopwatch.StartNew();
            var snap = full
                ? DumpCollector.CollectFull(dumpCtx, log.OnProgress)
                : DumpCollector.CollectLightweight(dumpCtx, log.OnProgress);

            string scoreLabel = snap.HealthScore >= 80 ? "HEALTHY" : snap.HealthScore >= 50 ? "DEGRADED" : "CRITICAL";
            string scoreColor = snap.HealthScore >= 80 ? "green" : snap.HealthScore >= 50 ? "yellow" : "red";
            log.Blank();
            log.CheckM(
                $"Collection complete  ({collSw.Elapsed.TotalSeconds:F1}s)  |  " +
                $"[{scoreColor}]{snap.HealthScore}/100  {scoreLabel}[/]  |  {snap.TotalObjectCount:N0} objs");

            log.Blank();
            log.SectionHeader("Rendering Output");

            using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0 ? a.EffectiveOutputPaths : null);
            AnalyzeReport.RenderReport(snap, sink, ctx: dumpCtx);
            log.Check("Summary report rendered.");

            if (full)
            {
                CommandBase.SetOverride("top",       strTop.ToString());
                CommandBase.SetOverride("min-count", strMinCnt.ToString());
                CommandBase.SetOverride("min-waste", strMinWaste.ToString());
                if (bfsExact)
                    CommandBase.SetSharedOverride("exact", "true");
                else if (bfsDepth.HasValue)
                    CommandBase.SetSharedOverride("bfs-depth", bfsDepth.Value.ToString());
                AnalyzeReport.RenderEmbeddedReports(dumpCtx, sink, log);
                CommandBase.ClearOverrides();
            }

            if (sink.IsFile)
            {
                log.Blank();
                foreach (var p in (a.EffectiveOutputPaths.Count > 0 ? (IEnumerable<string>)a.EffectiveOutputPaths : [sink.FilePath ?? outputPath ?? string.Empty]))
                    if (!string.IsNullOrEmpty(p) && !p.Equals("console", StringComparison.OrdinalIgnoreCase))
                        log.Success($"Written to: {p}");
            }
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

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        var snap = DumpCollector.CollectFull(ctx);
        AnalyzeReport.RenderReport(snap, sink, ctx: ctx);
    }
}
