using DumpDetective.Analysis.Memory;
using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Reports;
using Spectre.Console;
namespace DumpDetective.Commands.Memory;

public sealed class DominatorTreeCommand : ICommand
{
    private readonly DomTreeAnalyzer     _analyzer;
    private readonly DominatorTreeReport _report;

    public DominatorTreeCommand(DomTreeAnalyzer analyzer, DominatorTreeReport report)
    {
        _analyzer = analyzer;
        _report   = report;
    }

    public string Name               => "dominator-tree";
    public string Description        => "Lengauer-Tarjan dominator tree: exact retained memory per object type group.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Retention / Leak Signals";

    private const string Help = """
        Usage: DumpDetective dominator-tree <dump-file> [options]

        Builds (or loads) a Lengauer-Tarjan dominator tree over the full heap reference graph
        and shows which type groups exclusively retain the most memory.

        The dominator index (.idom.idx) is built once alongside the BFS index (.bfs.idx)
        and reused on subsequent runs.  First-time build requires an existing .bfs.idx.

        Options:
          -n, --top <N>          Top N type groups per level (default: 20)
          --depth <D>            Max display depth (default: 8)
          --min-size <MB>        Prune subtrees smaller than M MB (default: 1)
          -f, --force            Rebuild .idom.idx even if a valid one exists
          -o, --output <file>    Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help

        Note: building the dominator index requires the BFS index (.bfs.idx).
              Run 'memory-leak' or any command that builds the BFS cache first.
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        int    top      = a.GetInt("top",   20);
        int    depth    = a.GetInt("depth",  8);
        long   minMb    = a.GetInt("min-size", 1);
        bool   force    = a.HasFlag("force") || a.HasFlag("f");

        return CommandBase.Execute(a, (ctx, sink) =>
            RenderWith(ctx, sink, top, depth, minMb * (1L << 20), force));
    }

    public void Render(DumpContext ctx, IRenderSink sink) =>
        RenderWith(ctx, sink, 20, 8, 1L << 20, forceRebuild: false);

    private void RenderWith(
        DumpContext ctx, IRenderSink sink,
        int top, int depth, long minBytes, bool forceRebuild)
    {
        CommandBase.RenderHeader("Dominator Tree", ctx, sink);

        if (!CommandBase.EnsureCanWalkHeap(ctx.Heap, sink)) return;

        // ── Require BFS index ─────────────────────────────────────────────────

        string bfsCachePath = BfsIndexCache.CachePath(ctx.DumpPath);
        if (!BfsIndexCache.IsValid(bfsCachePath, ctx.DumpPath))
        {
            sink.Alert(AlertLevel.Warning,
                "BFS index not found",
                "The dominator-tree command requires a .bfs.idx file built from the same dump.",
                "Run 'memory-leak' first, or any command that builds the BFS cache.");
            return;
        }

        // ── Load dominator cache (shared via once-cache; pre-loaded by analyze --full) ─

        DomTreeCache? domCache = null;
        CommandBase.RunStatus("Loading/building dominator tree…", upd =>
            domCache = ctx.GetOrCreateAnalysis<DomTreeCacheBox>(() =>
                new DomTreeCacheBox(DomTreeBuilder.LoadOrBuild(ctx, forceRebuild, upd))).Cache);

        if (domCache is null)
        {
            sink.Alert(AlertLevel.Critical, "Failed to build dominator index",
                "An error occurred while computing the dominator tree.");
            return;
        }

        // ── BFS cache is now in the once-cache too (loaded by DomTreeBuilder) ──

        var bfsCache = ctx.GetOrCreateAnalysis<BfsCacheBox>(() =>
            new BfsCacheBox(BfsIndexCache.TryLoad(ctx.DumpPath))).Cache;

        if (bfsCache is null)
        {
            sink.Alert(AlertLevel.Critical, "Failed to load BFS index", null);
            return;
        }

        // ── Analyze and render ────────────────────────────────────────────────

        var data = _analyzer.Analyze(ctx, domCache, bfsCache, top, depth, minBytes);
        _report.Render(data, sink);
    }
}
