using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class PinnedObjectsCommand
{
    private const string Help = """
        Usage: DumpDetective pinned-objects <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        var pinned = ctx.Runtime.EnumerateHandles()
            .Where(h => h.IsPinned)
            .Select(h => { var obj = ctx.Heap.GetObject(h.Object); return (Type: obj.Type?.Name ?? "<unknown>", Addr: h.Object, Size: obj.IsValid ? (long)obj.Size : 0L); })
            .ToList();

        sink.Section("Pinned Objects");
        if (pinned.Count == 0) { sink.Text("No pinned GC handles found."); return; }

        var grouped = pinned.GroupBy(p => p.Type).OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(p => p.Size)) }).ToList();
        sink.Table(["Type", "Count", "Total Size"], grouped);

        if (showAddr)
        {
            var rows = pinned.Take(100).Select(p => new[] { p.Type, $"0x{p.Addr:X16}", DumpHelpers.FormatSize(p.Size) }).ToList();
            sink.Table(["Type", "Address", "Size"], rows);
        }

        if (pinned.Count > 2000) sink.Alert(AlertLevel.Warning, $"{pinned.Count:N0} pinned handles may cause fragmentation.",
            advice: "Replace GCHandle.Alloc(Pinned) with Memory<T>/MemoryPool<T>.");
        sink.KeyValues([("Pinned handles", pinned.Count.ToString("N0")), ("Total size", DumpHelpers.FormatSize(pinned.Sum(p => p.Size)))]);
    }
}
