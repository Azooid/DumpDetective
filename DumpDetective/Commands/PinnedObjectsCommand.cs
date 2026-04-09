using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class PinnedObjectsCommand
{
    private const string Help = """
        Usage: DumpDetective pinned-objects <dump-file> [options]

        Shows GCHandle.Alloc(Pinned) and async-I/O pinned handles.
        Pinned objects in Gen0/Gen1 prevent the GC from compacting those segments.

        Options:
          -a, --addresses    Show individual object addresses (up to 100 per type)
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

    private sealed record PinnedItem(
        string TypeName, ulong Addr, long Size, string Gen, bool IsAsyncPinned);

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
            "Dump Detective — Pinned Objects",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var items = new List<PinnedItem>();
        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            if (!h.IsPinned || h.Object == 0) continue;
            var obj  = ctx.Heap.GetObject(h.Object);
            string gen  = GetGenLabel(ctx, h.Object);
            bool async  = h.HandleKind != ClrHandleKind.Pinned;   // AsyncPinned or other pinned variant
            items.Add(new PinnedItem(
                obj.Type?.Name ?? "<unknown>",
                h.Object,
                obj.IsValid ? (long)obj.Size : 0L,
                gen,
                async));
        }

        sink.Section("Pinned Objects");
        if (items.Count == 0) { sink.Alert(AlertLevel.Info, "No pinned GC handles found."); return; }

        int  pinnedCount      = items.Count(i => !i.IsAsyncPinned);
        int  asyncPinnedCount = items.Count(i =>  i.IsAsyncPinned);
        long totalSize        = items.Sum(i => i.Size);
        int  inSohCount       = items.Count(i => i.Gen is "Gen0" or "Gen1" or "Gen2");

        // ── Summary key-values ────────────────────────────────────────────────
        sink.KeyValues([
            ("GCHandle.Pinned",         pinnedCount.ToString("N0")),
            ("Async-Pinned (I/O)",      asyncPinnedCount.ToString("N0")),
            ("Total pinned handles",    items.Count.ToString("N0")),
            ("Total size",              DumpHelpers.FormatSize(totalSize)),
            ("In SOH (Gen0/Gen1/Gen2)", inSohCount.ToString("N0")),
        ]);

        // ── Alerts ────────────────────────────────────────────────────────────
        if (items.Count >= 2000)
            sink.Alert(AlertLevel.Critical,
                $"{items.Count:N0} pinned handles — severe fragmentation risk.",
                $"{inSohCount:N0} are in SOH generations which prevents GC compaction.",
                "Replace GCHandle.Alloc(Pinned) with Memory<T>/MemoryPool<T>, or pin only at P/Invoke boundaries.");
        else if (items.Count >= 500)
            sink.Alert(AlertLevel.Warning,
                $"{items.Count:N0} pinned handles — notable fragmentation pressure.",
                $"{inSohCount:N0} in SOH. High pinned counts inflate heap fragmentation.",
                "Audit long-lived pinned arrays and consider fixed() statements scoped to the I/O call.");
        else if (items.Count >= 50)
            sink.Alert(AlertLevel.Info,
                $"{items.Count:N0} pinned handles — monitor for growth.",
                "Acceptable at low counts; watch for steady increase across snapshots.");

        // Highlight byte[] since these are the most common fragmentation source
        long byteArraySize = items.Where(i => i.TypeName == "System.Byte[]").Sum(i => i.Size);
        if (byteArraySize > 10 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning,
                $"{DumpHelpers.FormatSize(byteArraySize)} in pinned byte[] arrays.",
                "Byte[] is the most common pinned type from socket/file I/O.",
                "Use ArrayPool<byte>.Shared or PipeReader/PipeWriter to avoid pinning.");

        // ── Type breakdown ────────────────────────────────────────────────────
        var typeRows = items
            .GroupBy(i => i.TypeName)
            .OrderByDescending(g => g.Sum(i => i.Size))
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(i => i.Size)),
                g.Count(i =>  i.IsAsyncPinned).ToString("N0"),
                g.Count(i => !i.IsAsyncPinned).ToString("N0"),
            })
            .ToList();
        sink.Table(["Type", "Count", "Total Size", "Async-Pinned", "GC-Pinned"], typeRows, "Pinned objects by type");

        // ── Generation distribution ───────────────────────────────────────────
        var genRows = items
            .GroupBy(i => i.Gen)
            .OrderBy(g => GenSortKey(g.Key))
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(i => i.Size)),
            })
            .ToList();
        sink.Table(["Generation", "Count", "Total Size"], genRows,
            "Generation distribution — Gen0/Gen1/Gen2 pinning causes fragmentation");

        // ── Address detail ────────────────────────────────────────────────────
        if (showAddr)
        {
            foreach (var group in items.GroupBy(i => i.TypeName).OrderByDescending(g => g.Count()))
            {
                var addrRows = group
                    .Take(100)
                    .Select(i => new[]
                    {
                        $"0x{i.Addr:X16}",
                        DumpHelpers.FormatSize(i.Size),
                        i.Gen,
                        i.IsAsyncPinned ? "Async" : "GC",
                    })
                    .ToList();
                sink.BeginDetails($"{group.Key}  ({group.Count():N0} handle(s))", open: false);
                sink.Table(["Address", "Size", "Gen", "Kind"], addrRows);
                sink.EndDetails();
            }
        }
    }

    private static string GetGenLabel(DumpContext ctx, ulong addr)
    {
        var seg = ctx.Heap.GetSegmentByAddress(addr);
        if (seg is null) return "?";
        return seg.Kind switch
        {
            GCSegmentKind.Large    => "LOH",
            GCSegmentKind.Pinned   => "POH",
            GCSegmentKind.Frozen   => "Frozen",
            GCSegmentKind.Ephemeral => EphemeralGen(seg, addr),
            _                      => "Gen2",
        };
    }

    private static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        return "Gen2";
    }

    private static int GenSortKey(string gen) => gen switch
    {
        "Gen0" => 0, "Gen1" => 1, "Gen2" => 2, "LOH" => 3, "POH" => 4, _ => 5
    };
}
