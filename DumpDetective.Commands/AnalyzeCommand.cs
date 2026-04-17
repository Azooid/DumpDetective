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
          --full               Full combined report (scored summary + all sub-reports)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective analyze app.dmp
          DumpDetective analyze app.dmp --full --output full-report.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var    a          = CliArgs.Parse(args);
        bool   full       = a.HasFlag("full");
        string? dumpPath  = a.DumpPath;
        string? outputPath= a.OutputPath;

        if (dumpPath is null)       { AnsiConsole.MarkupLine("[bold red]✗[/] dump file path required."); return 1; }
        if (!File.Exists(dumpPath)) { AnsiConsole.MarkupLine($"[bold red]✗[/] file not found: {Markup.Escape(dumpPath)}"); return 1; }

        CommandBase.PrintAnalyzing(dumpPath);

        if (!full)
        {
            // ── Mini: lightweight collection + scored summary ─────────────────
            var snap = RunCollect("mini", dumpPath,
                upd => DumpCollector.CollectLightweight(dumpPath, upd));
            using var sink = SinkFactory.Create(outputPath);
            AnalyzeReport.RenderReport(snap, sink);
            if (sink.IsFile && sink.FilePath is not null)
                AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
            return 0;
        }

        // ── Full: open dump once — snapshot + all sub-reports ─────────────────
        try
        {
            using var dumpCtx = DumpContext.Open(dumpPath);
            if (dumpCtx.ArchWarning is not null)
                AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(dumpCtx.ArchWarning)}[/]");

            var snap = RunCollect("full", dumpPath,
                upd => DumpCollector.CollectFull(dumpCtx, upd));

            using var sink = SinkFactory.Create(outputPath);
            AnalyzeReport.RenderReport(snap, sink);
            AnsiConsole.MarkupLine("[bold blue]Embedding detailed sub-reports:[/]");
            AnalyzeReport.RenderEmbeddedReports(dumpCtx, sink);

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

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        var snap = DumpCollector.CollectFull(ctx);
        AnalyzeReport.RenderReport(snap, sink);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DumpSnapshot RunCollect(string mode, string dumpPath, Func<Action<string>, DumpSnapshot> collect)
    {
        DumpSnapshot snap = null!;
        var sw = Stopwatch.StartNew();
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start($"Running {mode} analysis — {Markup.Escape(Path.GetFileName(dumpPath))}...", ctx =>
                snap = collect(msg => ctx.Status($"[dim]{Markup.Escape(Path.GetFileName(dumpPath))}[/]  {Markup.Escape(msg)}")));
        AnsiConsole.MarkupLine($"[dim]  Collection complete ({sw.Elapsed.TotalSeconds:F1}s)[/]");
        return snap;
    }
}
