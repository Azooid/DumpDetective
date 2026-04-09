using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class WeakRefsCommand
{
    private const string Help = """
        Usage: DumpDetective weak-refs <dump-file> [options]

        Options:
          -a, --addresses    Show handle addresses
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
        var refs = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
            .Select(h => {
                bool alive = h.Object != 0;
                var obj = alive ? ctx.Heap.GetObject(h.Object) : default;
                return (Kind: h.HandleKind.ToString(), Alive: alive && obj.IsValid,
                        Type: alive && obj.IsValid ? obj.Type?.Name ?? "?" : "<collected>",
                        Addr: h.Object);
            }).ToList();

        int aliveCount     = refs.Count(r => r.Alive);
        int collectedCount = refs.Count - aliveCount;

        var summary = refs.Where(r => r.Alive).GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();

        sink.Section("Weak References");
        sink.KeyValues([
            ("Total weak handles", refs.Count.ToString("N0")),
            ("Alive",             aliveCount.ToString("N0")),
            ("Collected",         collectedCount.ToString("N0")),
        ]);
        if (summary.Count > 0) sink.Table(["Alive Type", "Count"], summary);

        if (showAddr && refs.Count > 0)
        {
            var rows = refs.Take(100).Select(r => new[] { r.Kind, r.Alive ? "Alive" : "Collected", r.Type, $"0x{r.Addr:X16}" }).ToList();
            sink.Table(["Kind", "Status", "Type", "Address"], rows);
        }
    }
}
