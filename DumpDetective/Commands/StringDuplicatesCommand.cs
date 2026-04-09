using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class StringDuplicatesCommand
{
    private const string Help = """
        Usage: DumpDetective string-duplicates <dump-file> [options]

        Options:
          -n, --top <N>           Show top N duplicate groups (default: 50)
          -c, --min-count <N>     Minimum duplicate count (default: 2)
          -w, --min-waste <bytes> Minimum wasted bytes to include
          -p, --pattern <hint>    Filter by pattern: url|guid|path|number|stackframe
          -o, --output <file>     Write report to file (.md / .html / .txt)
          -h, --help              Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 50; int minCount = 2; long minWaste = 0; string? pattern = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top"       or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-count" or "-c") && i + 1 < args.Length) int.TryParse(args[++i], out minCount);
            else if ((args[i] is "--min-waste" or "-w") && i + 1 < args.Length) long.TryParse(args[++i], out minWaste);
            else if ((args[i] is "--pattern"   or "-p") && i + 1 < args.Length) pattern = args[++i].ToLowerInvariant();
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minCount, minWaste, pattern));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
        int top = 50, int minCount = 2, long minWaste = 0, string? pattern = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — String Duplicates",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var groups = new Dictionary<string, (int Count, long TotalSize)>(StringComparer.Ordinal);
        long totalStrings = 0, totalSize = 0;

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning strings...", statusCtx =>
        {
            var watch = Stopwatch.StartNew();

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type?.Name != "System.String") continue;
                totalStrings++;
                long size   = (long)obj.Size;
                totalSize  += size;
                var val     = obj.AsString(maxLength: 512) ?? string.Empty;

                if (groups.TryGetValue(val, out var e)) groups[val] = (e.Count + 1, e.TotalSize + size);
                else                                    groups[val] = (1, size);

                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    statusCtx.Status($"Scanning strings — {totalStrings:N0} scanned, {groups.Count:N0} unique values...");
                    watch.Restart();
                }
            }
        });

        var candidates = groups
            .Where(kv => kv.Value.Count >= minCount)
            .Select(kv => {
                long perCopy = kv.Value.TotalSize / kv.Value.Count;
                long wasted  = perCopy * (kv.Value.Count - 1);
                string hint  = ClassifyString(kv.Key);
                return (Value: kv.Key, Count: kv.Value.Count, Total: kv.Value.TotalSize,
                        Wasted: wasted, Len: kv.Key.Length, Hint: hint);
            })
            .Where(r => r.Wasted >= minWaste)
            .Where(r => pattern is null || r.Hint.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Wasted)
            .Take(top)
            .ToList();

        int  dupGroups  = groups.Count(kv => kv.Value.Count >= 2);
        long wastedAll  = groups
            .Where(kv => kv.Value.Count >= 2)
            .Sum(kv => { long per = kv.Value.TotalSize / kv.Value.Count; return per * (kv.Value.Count - 1); });

        sink.Section("Summary");
        sink.KeyValues(
        [
            ("Total strings",    totalStrings.ToString("N0")),
            ("Total string size", DumpHelpers.FormatSize(totalSize)),
            ("Duplicate groups",  dupGroups.ToString("N0")),
            ("Total wasted",      DumpHelpers.FormatSize(wastedAll)),
        ]);

        if (wastedAll > 100 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"{DumpHelpers.FormatSize(wastedAll)} wasted on duplicate strings.",
                advice: "Use string.Intern() for high-frequency identifiers. Use a shared lookup table or frozen dictionary.");

        if (candidates.Count == 0) { sink.Text("No duplicate strings found matching the criteria."); return; }

        var rows = candidates.Select(r => {
            string display = r.Value.Length > 72 ? r.Value[..72] + "\u2026" : r.Value;
            display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return new[]
            {
                r.Count.ToString("N0"),
                DumpHelpers.FormatSize(r.Wasted),
                DumpHelpers.FormatSize(r.Total),
                r.Len.ToString("N0"),
                r.Hint.Length > 0 ? r.Hint : "—",
                $"\"{display}\"",
            };
        }).ToList();

        sink.Table(
            ["Count", "Wasted", "Total", "Length", "Pattern", "Value"],
            rows,
            $"Top {rows.Count} duplicate groups by wasted bytes" + (pattern is not null ? $" (filter={pattern})" : ""));
    }

    // ── Pattern classification ─────────────────────────────────────────────

    static string ClassifyString(string s)
    {
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("ftp://",   StringComparison.OrdinalIgnoreCase))
            return "url";
        if (Guid.TryParse(s, out _)) return "guid";
        if (s.StartsWith("   at ", StringComparison.Ordinal) ||
            (s.StartsWith("at ",   StringComparison.Ordinal) && s.Contains('(')))
            return "stackframe";
        if (s.Length > 2 && (s.Contains('\\') || s.Contains('/')) &&
            (s.Contains('.') || s.Contains(':')))
            return "path";
        if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
            return "number";
        return "";
    }
}