using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class StringDuplicatesCommand
{
    private const string Help = """
        Usage: DumpDetective string-duplicates <dump-file> [options]

        Options:
          -n, --top <N>           Show top N duplicate groups (default: 50)
          -c, --min-count <N>     Minimum duplicate count (default: 2)
          -w, --min-waste <bytes> Minimum wasted bytes to include
          -o, --output <file>     Write report to file (.md / .html / .txt)
          -h, --help              Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; int minCount = 2; long minWaste = 0;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)         int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-count" or "-c") && i + 1 < args.Length) int.TryParse(args[++i], out minCount);
            else if ((args[i] is "--min-waste" or "-w") && i + 1 < args.Length) long.TryParse(args[++i], out minWaste);
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minCount, minWaste));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 50, int minCount = 2, long minWaste = 0)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var groups = new Dictionary<string, (int Count, long TotalSize)>(StringComparer.Ordinal);
        long totalStrings = 0, totalSize = 0;

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning strings...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type?.Name != "System.String") continue;
                totalStrings++;
                long size = (long)obj.Size;
                totalSize += size;
                var val = obj.AsString(maxLength: 512) ?? string.Empty;
                if (groups.TryGetValue(val, out var e)) groups[val] = (e.Count + 1, e.TotalSize + size);
                else groups[val] = (1, size);
            }
        });

        var rows = groups
            .Where(kv => kv.Value.Count >= minCount)
            .Select(kv => {
                long per = kv.Value.TotalSize / kv.Value.Count;
                long wasted = per * (kv.Value.Count - 1);
                return (Value: kv.Key, Count: kv.Value.Count, Total: kv.Value.TotalSize, Wasted: wasted);
            })
            .Where(r => r.Wasted >= minWaste)
            .OrderByDescending(r => r.Wasted)
            .Take(top)
            .Select(r => {
                string display = r.Value.Length > 72 ? r.Value[..72] + "\u2026" : r.Value;
                display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                return new[] { r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Wasted), DumpHelpers.FormatSize(r.Total), $"\"{display}\"" };
            }).ToList();

        int dupGroups  = groups.Count(kv => kv.Value.Count >= 2);
        long wastedAll = groups.Where(kv => kv.Value.Count >= 2)
            .Sum(kv => { long per = kv.Value.TotalSize / kv.Value.Count; return per * (kv.Value.Count - 1); });

        sink.Section("String Duplicates");
        if (rows.Count == 0) { sink.Text("No duplicate strings found matching the criteria."); return; }
        sink.Table(["Count", "Wasted", "Total", "Value"], rows, $"Top {rows.Count} duplicate groups by wasted bytes");
        sink.KeyValues(
        [
            ("Total strings",     totalStrings.ToString("N0")),
            ("Total string size",  DumpHelpers.FormatSize(totalSize)),
            ("Duplicate groups",   dupGroups.ToString("N0")),
            ("Total wasted",       DumpHelpers.FormatSize(wastedAll)),
        ]);
    }
}