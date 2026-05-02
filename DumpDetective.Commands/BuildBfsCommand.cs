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
        Usage: DumpDetective build-bfs <dump-file-or-directory> [options]

        Builds a compressed forward-reference graph of the managed heap and saves it as
        <dump>.bfs.idx alongside the dump file.  Once built, 'object-inspect --retained'
        loads the cache instead of re-walking the heap — retained sizes compute instantly.

        When a directory is given, all .dmp and .mdmp files inside it are processed
        one at a time.

        Options:
          --force, -f    Rebuild even if a valid cache already exists
          --recurse, -r  When input is a directory, also search subdirectories
          -h, --help     Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? input = a.DumpPath ?? a.Positionals.FirstOrDefault();
        if (string.IsNullOrEmpty(input))
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Dump file or directory path is required.");
            return 1;
        }

        bool force   = a.HasFlag("force")   || a.HasFlag("f");
        bool recurse = a.HasFlag("recurse") || a.HasFlag("r");

        // Expand directory to a list of dump files.
        List<string> dumpPaths;
        if (Directory.Exists(input))
        {
            var searchOpt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            dumpPaths = [.. Directory
                .EnumerateFiles(input, "*", searchOpt)
                .Where(f => f.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)];

            if (dumpPaths.Count == 0)
            {
                AnsiConsole.MarkupLine($"[bold red]✗[/] No .dmp or .mdmp files found in: {Markup.Escape(input)}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[dim]Found {dumpPaths.Count} dump file(s) in {Markup.Escape(input)}[/]");
            AnsiConsole.WriteLine();
        }
        else if (File.Exists(input))
        {
            dumpPaths = [input];
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File or directory not found: {Markup.Escape(input)}");
            return 1;
        }

        int ok = 0, skipped = 0, failed = 0;
        for (int i = 0; i < dumpPaths.Count; i++)
        {
            var dumpPath = dumpPaths[i];
            if (dumpPaths.Count > 1)
                AnsiConsole.MarkupLine($"[bold]── [{i + 1}/{dumpPaths.Count}] {Markup.Escape(Path.GetFileName(dumpPath))}[/]");

            int result = BuildOne(dumpPath, force);
            if (result == 0) ok++;
            else if (result == 2) skipped++;
            else failed++;

            if (dumpPaths.Count > 1) AnsiConsole.WriteLine();
        }

        if (dumpPaths.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Done:[/] {ok} built, {skipped} skipped (up-to-date), {failed} failed  " +
                $"[dim]out of {dumpPaths.Count} dump(s)[/]");
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>Returns 0 = built, 1 = error, 2 = skipped (already valid).</summary>
    private static int BuildOne(string dumpPath, bool force)
    {
        string cachePath = BfsIndexCache.CachePath(dumpPath);

        if (!force && BfsIndexCache.IsValid(cachePath, dumpPath))
        {
            var existing = BfsIndexCache.Load(cachePath);
            AnsiConsole.MarkupLine(
                $"[green]✓[/] Cache already up to date: [dim]{cachePath}[/]");
            AnsiConsole.MarkupLine(
                $"  [dim]{existing.NodeCount:N0} nodes · {existing.EdgeCount:N0} edges[/]");
            AnsiConsole.MarkupLine(
                "  Use [bold]--force[/] to rebuild.");
            return 2;
        }

        return CommandBase.Execute(dumpPath, outputPaths: null, (ctx, _) =>
        {
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
