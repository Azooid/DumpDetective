namespace DumpDetective.Commands;

public sealed class ObjectInspectCommand : ICommand
{
    public string Name               => "object-inspect";
    public string Description        => "Deep-inspect a managed object by address: type, fields, gen, finalizer status.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective object-inspect <dump-file> --address <hex> [options]

        Options:
          --address, -x <addr>    Object address in hex (required)
          -d, --depth <N>         Recursion depth into references (default: 1, max: 5)
          --max-array <N>         Max array elements to display (default: 10)
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
        if (addrStr is null || !TryParseHex(addrStr, out address))
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] --address is required.");
            return 1;
        }
        depth = Math.Clamp(depth, 1, 5);

        return CommandBase.Execute(a.DumpPath, a.OutputPath,
            (ctx, sink) => RenderWith(ctx, sink, address, depth, maxArr));
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        sink.Alert(AlertLevel.Warning, "object-inspect requires --address — use the Run() entry point.");
    }


    private static void RenderWith(DumpContext ctx, IRenderSink sink, ulong address, int depth, int maxArray)
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
        ObjectInspectRenderer.Render(ctx, obj, sink, depth, 0, maxArray, finQueue, pinnedAddrs, visited);
    }

    private static bool TryParseHex(string s, out ulong value)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
