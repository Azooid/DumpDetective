using System.Diagnostics;
using DumpDetective.Analysis.Memory;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Commands.Memory;

/// <summary>
/// Scored health report for a single dump.
/// Mini (default): lightweight scored summary — fast.
/// Full (--full):  scored summary + all ICommand.IncludeInFullAnalyze sub-reports.
/// </summary>
public sealed class AnalyzeCommand : ICommand
{
    private readonly IReadOnlyList<ICommand> _builtInCommands;
    private readonly IReadOnlyList<ICommand> _pluginCommands;
    private readonly IReadOnlyDictionary<string, string>? _pluginCmdNames;

    public AnalyzeCommand(IReadOnlyList<ICommand> fullAnalyzeCommands, IReadOnlyList<ICommand>? pluginCommands = null, IReadOnlyDictionary<string, string>? pluginCmdNames = null)
    {
        _builtInCommands = fullAnalyzeCommands;
        _pluginCommands  = pluginCommands ?? [];
        _pluginCmdNames  = pluginCmdNames;
    }

    public string Name               => "analyze";
    public string Description        => "Scored health report for a single dump (use --full for all sub-reports).";
    public bool   IncludeInFullAnalyze => false;  // orchestrator — not nested
    public string Category             => "Orchestrator";

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
          --with-plugins           Include plugin commands in full-analyze (default: excluded)
          --persist                Keep all .ddcache temp files after analysis for re-use on next run
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
          DumpDetective analyze app.dmp --full --persist
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a          = CliArgs.Parse(args);
        bool    full       = a.HasFlag("full");
        bool    withPlugins= a.HasFlag("with-plugins");
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
            var (loadWs, loadMgd) = ToolMemoryDiagnostic.SampleForStep();
            using var dumpCtx = DumpContext.Open(dumpPath);
            ToolMemoryDiagnostic.RecordPipelineStep("Load dump", loadWs, loadMgd);
            var clrVer = dumpCtx.ClrVersion ?? "unknown";
            log.Success($"Dump loaded  |  CLR {clrVer}", indent: true);
            if (dumpCtx.ArchWarning is not null)
                log.Warn(dumpCtx.ArchWarning, indent: true);

            if (full && withPlugins && _pluginCommands.Count > 0)
            {
                log.InfoM(
                    $"[dim]  + [/][bold]{_pluginCommands.Count}[/][dim] plugin command(s) included via --with-plugins:[/] " +
                    string.Join(", ", _pluginCommands.Select(c => $"[bold]{Markup.Escape(c.Name)}[/]")),
                    indent: true);
            }

            log.Blank();
            log.SectionHeader("Collection");

            var collSw = Stopwatch.StartNew();
            var (collWs, collMgd) = ToolMemoryDiagnostic.SampleForStep();
            var effectiveCmds = (withPlugins && _pluginCommands.Count > 0)
                ? (IReadOnlyList<ICommand>)[.._builtInCommands, .._pluginCommands]
                : _builtInCommands;
            var heapContributors = full ? CollectHeapContributors(effectiveCmds) : null;
            var snap = full
                ? DumpCollector.CollectFull(dumpCtx, heapContributors ?? [], log.OnProgress)
                : DumpCollector.CollectLightweight(dumpCtx, log.OnProgress);
            ToolMemoryDiagnostic.RecordPipelineStep(full ? "Heap walk + scoring (full)" : "Heap walk + scoring", collWs, collMgd);

            string scoreLabel = snap.HealthScore >= 80 ? "HEALTHY" : snap.HealthScore >= 50 ? "DEGRADED" : "CRITICAL";
            string scoreColor = snap.HealthScore >= 80 ? "green" : snap.HealthScore >= 50 ? "yellow" : "red";
            log.Blank();
            log.CheckM(
                $"Collection complete  ({collSw.Elapsed.TotalSeconds:F1}s)  |  " +
                $"[{scoreColor}]{snap.HealthScore}/100  {scoreLabel}[/]  |  {snap.TotalObjectCount:N0} objs");

            log.Blank();
            log.SectionHeader("Rendering Output");

            using var sink = SinkFactory.CreateMulti(a.EffectiveOutputPaths.Count > 0 ? a.EffectiveOutputPaths : null);
            var (rptWs, rptMgd) = ToolMemoryDiagnostic.SampleForStep();
            AnalyzeReport.RenderReport(snap, sink, ctx: dumpCtx, canJumpToEvidence: full);
            ToolMemoryDiagnostic.RecordPipelineStep("Build summary", rptWs, rptMgd);
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

                // Pre-load (or build) the BFS index cache once on the main thread before
                // the parallel sub-report workers start.  Workers call
                // GetOrCreateAnalysis<BfsCacheBox>() which returns instantly from the
                // Lazy<T> already seeded here — no worker ever touches the file.
                var bfsCachePath = BfsIndexCache.CachePath(dumpPath);
                BfsIndexCache? bfsReady = null;
                var (bfsWs, bfsMgd) = ToolMemoryDiagnostic.SampleForStep();
                if (BfsIndexCache.IsValid(bfsCachePath, dumpPath))
                {
                    log.Info("Loading BFS index cache...", indent: true);
                    CommandBase.RunStatus("Loading BFS index...", update =>
                        bfsReady = BfsIndexCache.Load(bfsCachePath, update));
                    ToolMemoryDiagnostic.RecordPipelineStep("BFS cache load", bfsWs, bfsMgd);
                    log.Check($"BFS index loaded  ({bfsReady!.NodeCount:N0} nodes, {bfsReady.EdgeCount:N0} edges)", indent: true);
                }
                else
                {
                    log.Info("No BFS index found — building now (this runs once per dump)...", indent: true);
                    BfsPass1State p1 = null!;
                    BfsPass2State p2 = null!;
                    CommandBase.RunStatus("BFS pass 1/3 — enumerating objects...",
                        update => p1 = BfsIndexBuilder.BuildPass1(dumpCtx.Heap, update));
                    CommandBase.RunStatus($"BFS pass 2/3 — counting edges ({p1.NodeCount:N0} nodes)...",
                        update => p2 = BfsIndexBuilder.BuildPass2(dumpCtx.Heap, p1, update));
                    CommandBase.RunStatus($"BFS pass 3/3 — filling {p2.TotalEdges:N0} edges...",
                        update => bfsReady = BfsIndexBuilder.BuildPass3(dumpCtx.Heap, p2, update));
                    CommandBase.RunStatus("Saving BFS index...",
                        update => bfsReady!.Save(bfsCachePath, dumpPath, update));
                    ToolMemoryDiagnostic.RecordPipelineStep("BFS cache build", bfsWs, bfsMgd);
                    log.Check($"BFS index built and saved  ({bfsReady!.NodeCount:N0} nodes, {bfsReady.EdgeCount:N0} edges)", indent: true);
                }
                dumpCtx.PreloadAnalysis(new BfsCacheBox(bfsReady));

                // Pre-load dominator index; build it now if missing (BFS already in memory).
                var idomPath = DomTreeCache.CachePath(dumpPath);
                DomTreeCache? domReady = null;
                if (DomTreeCache.IsValid(idomPath, dumpPath))
                {
                    log.Info("Loading dominator index cache...", indent: true);
                    CommandBase.RunStatus("Loading dominator index...", update =>
                        domReady = DomTreeCache.TryLoad(dumpPath, update));
                }
                else
                {
                    log.Info("Building dominator index (Lengauer-Tarjan, 3 passes)...", indent: true);
                    DomTreeBuilder.Pass1State p1 = null!;
                    DomTreeBuilder.Pass2State p2 = null!;
                    CommandBase.RunStatus(
                        $"Dominator pass 1/3 — roots + graph ({bfsReady!.NodeCount:N0} nodes, {bfsReady.EdgeCount:N0} edges)...",
                        update =>
                        {
                            string label = $"Dominator pass 1/3 — roots + graph ({bfsReady.NodeCount:N0} nodes)";
                            using var ticker = new System.Threading.Timer(_ => update(label), null, 200, 200);
                            p1 = DomTreeBuilder.BuildGraph(dumpCtx, bfsReady, update);
                        });
                    CommandBase.RunStatus(
                        $"Dominator pass 2/3 — Lengauer-Tarjan ({p1.NodeCount:N0} nodes, {p1.GcRootCount:N0} roots)...",
                        update =>
                        {
                            // Timer fires every 200 ms to keep the elapsed-time display live
                            // (LT is a tight CPU loop that never yields to call update itself).
                            string label = $"Dominator pass 2/3 — Lengauer-Tarjan ({p1.NodeCount:N0} nodes)";
                            using var ticker = new System.Threading.Timer(_ => update(label), null, 200, 200);
                            p2 = DomTreeBuilder.RunLT(p1, update);
                            p1 = null!;
                        });
                    CommandBase.RunStatus(
                        "Dominator pass 3/3 — retained sizes + save...",
                        update => { domReady = DomTreeBuilder.FinalizeAndSave(p2, bfsReady, idomPath, dumpPath, update); p2 = null!; });
                }
                if (domReady is not null)
                    dumpCtx.PreloadAnalysis(new DomTreeCacheBox(domReady));

                var (subWs, subMgd) = ToolMemoryDiagnostic.SampleForStep();
                AnalyzeReport.RenderEmbeddedReports(dumpCtx, sink, effectiveCmds, log, _pluginCmdNames);
                ToolMemoryDiagnostic.RecordPipelineStep("Sub-reports (all)", subWs, subMgd);
                CommandBase.ClearOverrides();

                // Release the BFS cache after all sub-reports have consumed it so the
                // large CSR arrays (IndexToAddr, Sizes, Offsets, Children) can be collected.
                dumpCtx.TryReplaceAnalysis(new BfsCacheBox(null));
            }

            if (sink.IsFile)
            {
                log.Blank();
                var paths = a.EffectiveOutputPaths.Count > 0
                    ? (IEnumerable<string>)a.EffectiveOutputPaths
                    : [sink.FilePath ?? outputPath ?? string.Empty];
                foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p))
                        log.SuccessFile(p);
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

    private static IReadOnlyList<ICommandHeapContributor> CollectHeapContributors(IReadOnlyList<ICommand> commands)
        => [.. commands.OfType<ICommandHeapContributor>()];
}
