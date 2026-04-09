using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HandleTableCommand
{
    private const string Help = """
        Usage: DumpDetective handle-table <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        var byKind = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
        int total = 0;

        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            total++;
            var key = h.HandleKind.ToString();
            long size = 0;
            try { var obj = ctx.Heap.GetObject(h.Object); if (obj.IsValid) size = (long)obj.Size; } catch { }
            if (byKind.TryGetValue(key, out var e)) byKind[key] = (e.Count + 1, e.Size + size);
            else byKind[key] = (1, size);
        }

        var rows = byKind.OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new[] { kv.Key, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size) }).ToList();

        sink.Section("GC Handle Table");
        if (total == 0) { sink.Text("No GC handles found."); return; }
        sink.Table(["Handle Kind", "Count", "Referenced Size"], rows);
        sink.KeyValues([("Total handles", total.ToString("N0"))]);
    }
}
