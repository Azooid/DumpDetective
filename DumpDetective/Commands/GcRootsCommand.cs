using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class GcRootsCommand
{
    private const string Help = """
        Usage: DumpDetective gc-roots <dump-file> --type <typename> [options]

        Options:
          -t, --type <name>       Type name to trace (case-insensitive)  [required]
          -n, --max-results <N>   Max instances to trace (default: 10)
          -a, --addresses         Show addresses
          -o, --output <f>        Write report to file
          -h, --help              Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName = null; int maxResults = 10; bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--type" or "-t") && i + 1 < args.Length) typeName = args[++i];
            else if ((args[i] is "--max-results" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out maxResults);
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }
        if (typeName is null) { AnsiConsole.MarkupLine("[bold red]✗[/] --type is required."); return 1; }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, typeName, maxResults, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, string typeName, int maxResults = 10, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var targets = new List<ClrObject>();
        var referencedBy = new Dictionary<ulong, List<ulong>>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Building reference map...", status =>
        {
            status.Status("Finding target objects...");
            targets = ctx.Heap.EnumerateObjects()
                .Where(o => o.IsValid && o.Type?.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) == true)
                .Take(maxResults)
                .ToList();

            if (targets.Count == 0) return;
            var targetAddrs = targets.Select(o => o.Address).ToHashSet();

            status.Status("Walking all references...");
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;
                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (!targetAddrs.Contains(refAddr)) continue;
                    if (!referencedBy.TryGetValue(refAddr, out var list))
                        referencedBy[refAddr] = list = [];
                    list.Add(obj.Address);
                }
            }
        });

        sink.Section($"GC Roots: {typeName}");
        if (targets.Count == 0) { sink.Text($"No instances of '{typeName}' found."); return; }

        var gcRoots = ctx.Heap.EnumerateRoots().Where(r => targets.Any(t => t.Address == r.Object)).ToList();

        var rootRows = gcRoots
            .GroupBy(r => r.RootKind.ToString())
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();

        sink.KeyValues([
            ("Matching instances",  targets.Count.ToString("N0")),
            ("GC roots pointing at", gcRoots.Count.ToString("N0")),
            ("Referrers",           referencedBy.Values.Sum(l => l.Count).ToString("N0")),
        ]);

        if (rootRows.Count > 0) sink.Table(["Root Kind", "Count"], rootRows, "GC roots by kind");

        if (showAddr)
        {
            var rows = targets.Select(o => new[] {
                o.Type?.Name ?? "?",
                $"0x{o.Address:X16}",
                DumpHelpers.FormatSize((long)o.Size),
                referencedBy.TryGetValue(o.Address, out var refs) ? refs.Count.ToString() : "0",
            }).ToList();
            sink.Table(["Type", "Address", "Size", "Referrers"], rows);
        }
    }
}
