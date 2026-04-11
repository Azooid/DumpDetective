using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HandleTableCommand
{
    private const string Help = """
        Usage: DumpDetective handle-table <dump-file> [options]

        Options:
          -n, --top <N>       Top N object types per handle kind (default: 5)
          -f, --filter <k>    Only show handles whose kind contains <k>
          -o, --output <f>    Write report to file (.html / .md / .txt / .json)
          -h, --help          Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        int top = 5; string? filter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)         int.TryParse(args[++i], out top);
            else if ((args[i] is "--filter" or "-f") && i + 1 < args.Length) filter = args[++i];
        }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, filter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int topN = 5, string? filter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — GC Handle Table",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        // kind → (TotalCount, TotalSize, typeName → (Count, Size))
        var byKind = new Dictionary<string, KindInfo>(StringComparer.Ordinal);
        int total  = 0;

        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            total++;
            var kind = h.HandleKind.ToString();
            if (filter != null && !kind.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            if (!byKind.TryGetValue(kind, out var info))
                info = new KindInfo();

            info.Count++;

            if (h.Object != 0)
            {
                try
                {
                    var obj = ctx.Heap.GetObject(h.Object);
                    if (obj.IsValid)
                    {
                        long size     = (long)obj.Size;
                        string typeName = obj.Type?.Name ?? "<unknown>";
                        info.TotalSize += size;
                        info.Types.TryGetValue(typeName, out var ts);
                        info.Types[typeName] = (ts.Count + 1, ts.Size + size);
                    }
                }
                catch { }
            }

            byKind[kind] = info;
        }

        sink.Section("Handle Summary");
        if (total == 0) { sink.Text("No GC handles found."); return; }

        var summaryRows = byKind
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new[]
            {
                kv.Key,
                kv.Value.Count.ToString("N0"),
                DumpHelpers.FormatSize(kv.Value.TotalSize),
            })
            .ToList();
        sink.Table(["Handle Kind", "Count", "Referenced Size"], summaryRows,
            $"{total:N0} total handles");
        sink.KeyValues([("Total handles", total.ToString("N0"))]);

        // ── Strong handle size alert ──────────────────────────────────────────
        if (byKind.TryGetValue("Strong", out var strongInfo) && strongInfo.TotalSize > 500 * 1024 * 1024L)
            sink.Alert(AlertLevel.Critical,
                $"Strong handles reference {DumpHelpers.FormatSize(strongInfo.TotalSize)} of live objects.",
                advice: "Review GCHandle.Alloc(obj, GCHandleType.Normal) usage — these prevent GC of the entire retained graph.");

        // ── Per-kind type breakdown ───────────────────────────────────────────
        sink.Section("Per-Kind Type Breakdown");
        foreach (var kv in byKind.OrderByDescending(k => k.Value.Count))
        {
            long kindSize = kv.Value.TotalSize;
            sink.BeginDetails(
                $"{kv.Key}  —  {kv.Value.Count:N0} handle(s)  |  {DumpHelpers.FormatSize(kindSize)}",
                open: kv.Key is "Strong" or "Pinned");

            var typeRows = kv.Value.Types
                .OrderByDescending(t => t.Value.Count)
                .Take(topN)
                .Select(t => new[]
                {
                    t.Key,
                    t.Value.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.Value.Size),
                })
                .ToList();
            if (typeRows.Count > 0)
                sink.Table(["Object Type", "Count", "Size"], typeRows,
                    $"Top {typeRows.Count} types under {kv.Key} handles");
            else
                sink.Text("  (no object type info available)");

            sink.EndDetails();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class KindInfo
    {
        public int    Count;
        public long   TotalSize;
        public readonly Dictionary<string, (int Count, long Size)> Types =
            new(StringComparer.Ordinal);
    }
}
