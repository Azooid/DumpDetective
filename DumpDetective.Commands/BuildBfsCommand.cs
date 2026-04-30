namespace DumpDetective.Commands;

/// <summary>
/// Pre-builds and saves a BFS index cache (<c>.bfs.idx</c>) for a dump file so that
/// subsequent <c>object-inspect --retained</c> runs load instantly instead of rebuilding.
/// </summary>
public sealed class BuildBfsCommand : ICommand
{
    public string Name               => "build-bfs";
    public string Description        => "Pre-build the BFS retained-size index cache (.bfs.idx) for a dump file.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective build-bfs <dump-file> [options]

        Builds a compressed forward-reference graph of the managed heap and saves it as
        <dump>.bfs.idx alongside the dump file.  Once built, 'object-inspect --retained'
        loads the cache instead of re-walking the heap — retained sizes compute instantly.

        Options:
          --force, -f    Rebuild even if a valid cache already exists
          -h, --help     Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? dumpPath = a.DumpPath;
        if (string.IsNullOrEmpty(dumpPath))
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Dump file path is required.");
            return 1;
        }

        bool force = a.HasFlag("force") || a.HasFlag("f");

        return CommandBase.Execute(dumpPath, outputPaths: null, (ctx, _) =>
        {
            string cachePath = BfsIndexCache.CachePath(dumpPath);

            if (!force && BfsIndexCache.IsValid(cachePath, dumpPath))
            {
                // Load just to report stats — no rebuild needed.
                var existing = BfsIndexCache.Load(cachePath);
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Cache already up to date: [dim]{cachePath}[/]");
                AnsiConsole.MarkupLine(
                    $"  [dim]{existing.NodeCount:N0} nodes · {existing.EdgeCount:N0} edges[/]");
                AnsiConsole.MarkupLine(
                    "  Use [bold]--force[/] to rebuild.");
                return;
            }

            BfsPass1State p1    = null!;
            BfsPass2State p2    = null!;
            BfsIndexCache cache = null!;

            CommandBase.RunStatus(
                $"BFS pass 1/3 — enumerating objects...",
                update => p1 = BfsIndexBuilder.BuildPass1(ctx.Heap, update));

            CommandBase.RunStatus(
                $"BFS pass 2/3 — counting edges ({p1.NodeCount:N0} nodes, {DumpHelpers.FormatSize(p1.TotalBytes)})...",
                update => p2 = BfsIndexBuilder.BuildPass2(ctx.Heap, p1, update));

            CommandBase.RunStatus(
                $"BFS pass 3/3 — filling {p2.TotalEdges:N0} edges...",
                update => cache = BfsIndexBuilder.BuildPass3(ctx.Heap, p2, update));

            CommandBase.RunStatus(
                $"Saving BFS index ({cache.NodeCount:N0} nodes, {cache.EdgeCount:N0} edges)...",
                update => cache.Save(cachePath, dumpPath, update));

            var fi = new System.IO.FileInfo(cachePath);
            AnsiConsole.MarkupLine(
                $"[green]✓[/] BFS index saved: [dim]{cachePath}[/]");
            AnsiConsole.MarkupLine(
                $"  [dim]{cache.NodeCount:N0} nodes · {cache.EdgeCount:N0} edges · " +
                $"{DumpHelpers.FormatSize(fi.Length)} on disk[/]");
        });
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => sink.Alert(AlertLevel.Warning, "build-bfs does not produce a report — use the Run() entry point.");
}
