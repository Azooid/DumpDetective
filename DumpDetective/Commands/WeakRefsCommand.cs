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

        sink.Header(
            "Dump Detective — Weak References",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var refs = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
            .Select(h =>
            {
                bool alive = h.Object != 0;
                var obj = alive ? ctx.Heap.GetObject(h.Object) : default;
                bool valid = alive && obj.IsValid;
                return (
                    Kind:  h.HandleKind.ToString(),
                    Alive: valid,
                    Type:  valid ? obj.Type?.Name ?? "?" : "<collected>",
                    Addr:  h.Object
                );
            }).ToList();

        int aliveCount     = refs.Count(r => r.Alive);
        int collectedCount = refs.Count - aliveCount;
        int total          = refs.Count;
        int alivePercent   = total > 0 ? aliveCount * 100 / total : 0;

        sink.Section("Weak Reference Summary");
        sink.KeyValues([
            ("Total weak handles", total.ToString("N0")),
            ("Alive",              $"{aliveCount:N0}  ({alivePercent}%)"),
            ("Collected",          collectedCount.ToString("N0")),
        ]);

        if (total > 10 && alivePercent < 20)
            sink.Alert(AlertLevel.Warning,
                $"Only {alivePercent}% of weak references are alive — high object churn or abandoned caches.",
                advice: "Review WeakReference<T> / ConditionalWeakTable usage. Objects may be collected sooner than expected.");
        else if (total > 1000)
            sink.Alert(AlertLevel.Info, $"{total:N0} weak handles in use.");

        // Alive type breakdown
        var aliveByType = refs.Where(r => r.Alive)
            .GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count())
            .Take(30)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (aliveByType.Count > 0)
            sink.Table(["Alive Object Type", "Count"], aliveByType, "Top types currently alive via weak reference");

        // Collected handles
        int collectedByShort = refs.Count(r => !r.Alive && r.Kind == "WeakShort");
        int collectedByLong  = refs.Count(r => !r.Alive && r.Kind == "WeakLong");
        if (collectedCount > 0)
            sink.KeyValues([
                ("Collected (WeakShort)", collectedByShort.ToString("N0")),
                ("Collected (WeakLong)",  collectedByLong.ToString("N0")),
                ("Note", "Collected object types are unavailable — object graphs were reclaimed by GC"),
            ]);

        if (showAddr && refs.Count > 0)
        {
            var rows = refs.Take(200).Select(r => new[]
            {
                r.Kind,
                r.Alive ? "Alive" : "Collected",
                r.Type,
                $"0x{r.Addr:X16}",
            }).ToList();
            sink.Table(["Kind", "Status", "Type", "Address"], rows, $"First {rows.Count} handles");
        }
    }
}
