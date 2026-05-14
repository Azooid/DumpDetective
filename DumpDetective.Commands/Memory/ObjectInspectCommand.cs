namespace DumpDetective.Commands.Memory;

public sealed class ObjectInspectCommand : ICommand
{
    public string Name               => "object-inspect";
    public string Description        => "Deep-inspect a managed object by address: type, fields, gen, finalizer status.";
    public bool   IncludeInFullAnalyze => false;
    public string Category             => "Targeted / Interactive";

    private const string Help = """
        Usage: DumpDetective object-inspect <dump-file> --address <hex> [options]

        Options:
          --address, -x <addr>    Object address in hex (required)
          -d, --depth <N>         Recursion depth into references (default: 5)
          --max-array <N>         Max array elements to display (default: 10)
          --retained, -r          Compute retained size per reference field via BFS (slower)
          --retained-cap <N>      BFS node cap per field when --retained is set (default: unlimited)
          --no-cache              Force rebuild the BFS index even if a valid cache exists
          --no-save               Skip saving the BFS index after building (default: always save)
          -o, --output <f>        Write report to file (.html / .md / .txt / .json)
          -h, --help              Show this help
        """;

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        if (CommandBase.TryHelp(args, Help)) return 0;

        ulong address = 0;
        int   depth   = a.GetInt("depth", 1);
        int   maxArr  = a.GetInt("max-array", 10);

        var addrStr = a.GetOption("address") ?? a.GetOption("x");
        if (addrStr is null || !DumpHelpers.TryParseHex(addrStr, out address))
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --address is required.");
            return 1;
        }
        depth = Math.Max(1, depth);

        bool retained    = a.HasFlag("retained") || a.HasFlag("r");
        long retainedCap = a.GetInt("retained-cap", 0); // 0 = unlimited
        bool noCache     = a.HasFlag("no-cache");
        bool noSave      = a.HasFlag("no-save");
        // When --retained is set without an explicit --depth, auto-drill to max depth
        // so the trace follows the heaviest field all the way down.
        if (retained && a.GetOption("depth") is null && a.GetOption("d") is null)
            depth = 5;

        string? dumpPath = a.DumpPath;
        return CommandBase.Execute(dumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, dumpPath, address, depth, maxArr, retained, retainedCap, noCache, noSave));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        sink.Alert(AlertLevel.Warning, "object-inspect requires --address — use the Run() entry point.");
    }


    private static void RenderWith(DumpContext ctx, IRenderSink sink, string? dumpPath,
                                    ulong address, int depth, int maxArray,
                                    bool retained, long retainedCap, bool noCache, bool noSave)
    {
        CommandBase.RenderHeader("Object Inspector", ctx, sink);

        var obj = ctx.Heap.GetObject(address);
        if (!obj.IsValid)
        {
            sink.Alert(AlertLevel.Warning, $"No valid managed object at 0x{address:X16}",
                "Address may point to unmanaged memory, free space, or an invalid location.");
            return;
        }

        var finQueue     = new HashSet<ulong>(ctx.Heap.EnumerateFinalizableObjects().Select(o => o.Address));
        var pinnedAddrs  = ctx.Runtime.EnumerateHandles()
            .Where(h => h.IsPinned && h.Object != 0)
            .Select(h => h.Object.Address)
            .ToHashSet();
        var visited = new HashSet<ulong>();

        sink.Section($"Object @ 0x{address:X16}");
        if (retained)
        {
            // ── Try to load or build a BFS index cache ──────────────────────
            BfsIndexCache? cache = null;
            if (dumpPath is not null)
            {
                string cachePath = BfsIndexCache.CachePath(dumpPath);
                if (!noCache && BfsIndexCache.IsValid(cachePath, dumpPath))
                {
                    CommandBase.RunStatus("Loading BFS index...", update =>
                        cache = BfsIndexCache.Load(cachePath, update));
                    AnsiConsole.MarkupLine(
                        $"  [dim]BFS index loaded — {cache!.NodeCount:N0} nodes, {cache.EdgeCount:N0} edges[/]");
                }
                else
                {
                    string saveNote = noSave ? "" : " (saving for future runs)";
                    CommandBase.RunStatus($"Building BFS index{saveNote}...", update =>
                    {
                        cache = BfsIndexBuilder.Build(ctx.Heap, update);
                        if (!noSave)
                        {
                            update($"Saving BFS index ({cache.NodeCount:N0} nodes, {cache.EdgeCount:N0} edges)...");
                            cache.Save(cachePath, dumpPath, update);
                        }
                    });
                    AnsiConsole.MarkupLine(
                        $"  [dim]BFS index built — {cache!.NodeCount:N0} nodes, {cache.EdgeCount:N0} edges[/]");
                }
            }

            // ── Pre-compute per-field retained sizes via cache ───────────────
            IReadOnlyDictionary<string, (long Bytes, bool Estimated)>? preRetained = null;
            if (cache is not null)
            {
                string capLabel = retainedCap <= 0 ? "unlimited" : $"{retainedCap:N0} nodes/field";
                CommandBase.RunStatus($"Computing retained sizes ({capLabel})...", update =>
                    preRetained = BfsIndexBuilder.ComputeRetainedForFields(cache, obj, retainedCap, update));
            }

            // ── Render: instant when preRetained is available, BFS fallback otherwise ─
            if (preRetained is not null)
            {
                CommandBase.RunStatus("Rendering object tree...", update =>
                    ObjectInspectRenderer.Render(ctx, obj, sink, depth, 0, maxArray, finQueue, pinnedAddrs, visited,
                                                 retained, retainedCap, update, preRetained,
                                                 retainedResolver: cache is null ? null
                                                     : child => BfsIndexBuilder.ComputeRetainedForFields(cache, child, retainedCap, update)));
            }
            else
            {
                string capLabel = retainedCap <= 0 ? "unlimited" : $"{retainedCap:N0} nodes/field";
                CommandBase.RunStatus($"Retained-size analysis ({capLabel})...", update =>
                    ObjectInspectRenderer.Render(ctx, obj, sink, depth, 0, maxArray, finQueue, pinnedAddrs, visited,
                                                 retained, retainedCap, update));
            }
        }
        else
        {
            ObjectInspectRenderer.Render(ctx, obj, sink, depth, 0, maxArray, finQueue, pinnedAddrs, visited,
                                         retained, retainedCap);
        }
    }
}
