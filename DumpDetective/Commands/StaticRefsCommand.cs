using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class StaticRefsCommand
{
    private const string Help = """
        Usage: DumpDetective static-refs <dump-file> [options]

        Options:
          -f, --filter <t>     Only types/fields whose name contains <t>
          -e, --exclude <t>    Exclude types containing <t> (repeatable)
          -a, --addresses      Show object addresses
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? filter = null; bool showAddr = false;
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
            else if ((args[i] is "--exclude" or "-e") && i + 1 < args.Length) excludes.Add(args[++i]);
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, filter, excludes, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
        string? filter = null, HashSet<string>? excludes = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Static Reference Fields",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        // Enumerate via module type-def map — covers all types, even those with no live instances
        // (unlike the old heap-object approach which missed never-instantiated static classes).
        var byDeclType     = new Dictionary<string, List<(long Size, string[] Row)>>(StringComparer.Ordinal);
        var sizeByDeclType = new Dictionary<string, long>(StringComparer.Ordinal);
        int total = 0;
        long totalSize = 0;

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning static fields...", _ =>
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            {
                foreach (var module in appDomain.Modules)
                {
                    foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
                    {
                        if (mt == 0) continue;
                        var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                        if (clrType is null) continue;

                        string declType = clrType.Name ?? "<unknown>";
                        if (DumpHelpers.IsSystemType(declType)) continue;
                        if (excludes?.Any(e => declType.Contains(e, StringComparison.OrdinalIgnoreCase)) == true) continue;

                        foreach (var field in clrType.StaticFields)
                        {
                            if (!field.IsObjectReference) continue;
                            string fieldName = field.Name ?? "<unknown>";

                            if (filter is not null &&
                                !declType.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                                !fieldName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                            try
                            {
                                var value = field.ReadObject(appDomain);
                                if (value.IsNull || !value.IsValid) continue;
                                string valType  = value.Type?.Name ?? "?";
                                long   retained = RetainedSize(value, ctx.Heap);
                                string sizeStr  = DumpHelpers.FormatSize(retained);
                                string isCol    = IsCollection(valType) ? "✓" : "—";
                                string addrStr  = showAddr ? $"0x{value.Address:X16}" : "";

                                var row = showAddr
                                    ? new[] { fieldName, valType, sizeStr, isCol, addrStr }
                                    : new[] { fieldName, valType, sizeStr, isCol };

                                if (!byDeclType.TryGetValue(declType, out var list))
                                {
                                    list = [];
                                    byDeclType[declType] = list;
                                }
                                list.Add((retained, row));
                                total++;
                                totalSize += retained;
                                sizeByDeclType[declType] = sizeByDeclType.GetValueOrDefault(declType) + retained;
                            }
                            catch { }
                        }
                    }
                }
            }
        });

        sink.Section("Non-Null Static Reference Fields");
        if (total == 0) { sink.Text("No non-null static reference fields found."); return; }

        sink.KeyValues([
            ("Declaring types",        byDeclType.Count.ToString("N0")),
            ("Static fields",          total.ToString("N0")),
            ("Total retained size",    DumpHelpers.FormatSize(totalSize)),
            ("Largest declaring type", sizeByDeclType.Count > 0
                                            ? $"{sizeByDeclType.MaxBy(kv => kv.Value).Key.Split('.').Last()}  ({DumpHelpers.FormatSize(sizeByDeclType.MaxBy(kv => kv.Value).Value)})"
                                            : "—"),
            ("Collection fields",      byDeclType.Values.SelectMany(v => v).Count(r => r.Row[3] == "✓").ToString("N0")),
        ]);

        sink.Alert(AlertLevel.Info,
            "Static object references are permanent GC roots — they keep entire object graphs alive for the process lifetime.",
            advice: "Prefer scoped DI registrations over static state. Use WeakReference<T> for caches.");

        string[] headers = showAddr
            ? ["Field", "Value Type", "Size", "Collection?", "Address"]
            : ["Field", "Value Type", "Size", "Collection?"];

        foreach (var kvp in byDeclType.OrderByDescending(kv => sizeByDeclType.GetValueOrDefault(kv.Key)))
        {
            bool hasCollection = kvp.Value.Any(r => r.Row[3] == "✓");
            sizeByDeclType.TryGetValue(kvp.Key, out long declSize);
            var sortedRows = kvp.Value
                .OrderByDescending(r => r.Size)
                .Select(r => r.Row)
                .ToList();
            sink.BeginDetails(
                $"{kvp.Key}  —  {kvp.Value.Count} field(s)  {DumpHelpers.FormatSize(declSize)}" + (hasCollection ? "  ⚠ has collection" : ""),
                open: hasCollection || kvp.Value.Count > 5);
            sink.Table(headers, sortedRows);
            sink.EndDetails();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // BFS walk from root — returns sum of sizes of all reachable objects.
    // Each field gets its own visited set so independent roots are counted fully.
    static long RetainedSize(ClrObject root, ClrHeap heap)
    {
        if (!root.IsValid) return 0;
        var visited = new HashSet<ulong> { root.Address };
        var queue   = new Queue<ClrObject>();
        queue.Enqueue(root);
        long total  = 0;
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            if (!obj.IsValid || obj.Type is null) continue;
            total += (long)obj.Size;
            foreach (var child in obj.EnumerateReferences())
            {
                if (child.IsValid && visited.Add(child.Address))
                    queue.Enqueue(child);
            }
        }
        return total;
    }

    static bool IsCollection(string typeName) =>
        typeName.Contains("List<",       StringComparison.Ordinal) ||
        typeName.Contains("Dictionary<", StringComparison.Ordinal) ||
        typeName.Contains("HashSet<",    StringComparison.Ordinal) ||
        typeName.Contains("Queue<",      StringComparison.Ordinal) ||
        typeName.Contains("Stack<",      StringComparison.Ordinal) ||
        typeName.Contains("ConcurrentDictionary<", StringComparison.Ordinal) ||
        typeName.EndsWith("[]",          StringComparison.Ordinal);
}
