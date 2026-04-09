using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HeapFragmentationCommand
{
    private const string Help = """
        Usage: DumpDetective heap-fragmentation <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file (.md / .html / .txt)
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

        sink.Header(
            "Dump Detective — Heap Fragmentation",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // Build per-segment data keyed by address
        var segData = new Dictionary<ulong, SegInfo>();
        foreach (var seg in ctx.Heap.Segments)
        {
            segData[seg.Address] = new SegInfo(
                DumpHelpers.SegmentKindLabel(ctx.Heap, seg.Address),
                seg.Address,
                (long)seg.CommittedMemory.Length);
        }

        // Count pinned handles per segment
        foreach (var h in ctx.Runtime.EnumerateHandles())
        {
            if (h.HandleKind != ClrHandleKind.Pinned || h.Object == 0) continue;
            var seg = ctx.Heap.GetSegmentByAddress(h.Object);
            if (seg is not null && segData.TryGetValue(seg.Address, out var info))
            {
                info.PinnedCount++;
                segData[seg.Address] = info;
            }
        }

        // Walk heap to measure live vs. free bytes per segment
        var freeType = ctx.Heap.FreeType;
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Measuring fragmentation...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;
                var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                if (seg is null || !segData.ContainsKey(seg.Address)) continue;
                var info = segData[seg.Address];
                long size = (long)obj.Size;
                if (obj.Type == freeType) info.FreeBytes  += size;
                else                      info.LiveBytes  += size;
                segData[seg.Address] = info;
            }
        });

        var allSegs = segData.Values
            .Where(s => s.CommittedBytes > 0)
            .OrderByDescending(s => s.CommittedBytes > 0 ? s.FreeBytes * 100.0 / s.CommittedBytes : 0)
            .ToList();

        long totalCommitted = allSegs.Sum(s => s.CommittedBytes);
        long totalFree      = allSegs.Sum(s => s.FreeBytes);
        double totalFrag    = totalCommitted > 0 ? totalFree * 100.0 / totalCommitted : 0;

        sink.Section("Overall Fragmentation");
        sink.KeyValues([
            ("Total committed",    DumpHelpers.FormatSize(totalCommitted)),
            ("Total live",         DumpHelpers.FormatSize(allSegs.Sum(s => s.LiveBytes))),
            ("Total free",         DumpHelpers.FormatSize(totalFree)),
            ("Overall frag %",     $"{totalFrag:F1}%"),
            ("Total pinned",       allSegs.Sum(s => s.PinnedCount).ToString("N0")),
        ]);

        if (totalFrag >= 40)
            sink.Alert(AlertLevel.Critical, $"Heap fragmentation critical: {totalFrag:F1}%",
                advice: "Reduce GCHandle.Alloc(Pinned) usage. Use MemoryPool<T> / ArrayPool<T> for I/O buffers. Enable Server GC for large workloads.");
        else if (totalFrag >= 20)
            sink.Alert(AlertLevel.Warning, $"Heap fragmentation elevated: {totalFrag:F1}%");

        // Per-segment table
        var rows = allSegs.Select(s => {
            double frag = s.CommittedBytes > 0 ? s.FreeBytes * 100.0 / s.CommittedBytes : 0;
            return new[]
            {
                $"0x{s.Address:X}",
                s.Kind,
                DumpHelpers.FormatSize(s.CommittedBytes),
                DumpHelpers.FormatSize(s.LiveBytes),
                DumpHelpers.FormatSize(s.FreeBytes),
                $"{frag:F1}%",
                s.PinnedCount.ToString("N0"),
            };
        }).ToList();
        sink.Table(
            ["Segment Addr", "Kind", "Committed", "Live", "Free", "Frag %", "Pinned"],
            rows, $"{allSegs.Count} segment(s)");

        // Per-segment alerts for hotspot segments
        foreach (var s in allSegs)
        {
            if (s.CommittedBytes <= 0) continue;
            double frag = s.FreeBytes * 100.0 / s.CommittedBytes;
            if (frag >= 50)
                sink.Alert(AlertLevel.Warning,
                    $"Segment 0x{s.Address:X} ({s.Kind}) is {frag:F0}% fragmented — {s.PinnedCount:N0} pinned object(s)",
                    advice: s.PinnedCount > 0
                        ? "Pinned objects prevent compaction. Minimise GCHandle.Alloc(Pinned) lifetime."
                        : "High free-to-committed ratio. Consider GC.Collect(2, GCCollectionMode.Aggressive) if this is a background issue.");
        }

        // Free-object distribution — top types by free-space consumption
        sink.Section("Free Object (Holes) Distribution");
        var freeObjStats = new Dictionary<int, (long Count, long Size)>();
        foreach (var obj in ctx.Heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type != freeType) continue;
            long sz = (long)obj.Size;
            // Bucket by size order of magnitude
            int bucket = sz switch
            {
                < 128        => 0,
                < 1024       => 1,
                < 4096       => 2,
                < 65536      => 3,
                < 1048576    => 4,
                _            => 5,
            };
            if (!freeObjStats.TryGetValue(bucket, out var bv)) bv = (0, 0);
            freeObjStats[bucket] = (bv.Count + 1, bv.Size + sz);
        }
        if (freeObjStats.Count > 0)
        {
            var freeRows = freeObjStats
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    string range = kv.Key switch
                    {
                        0 => "< 128 B",
                        1 => "128 B – 1 KB",
                        2 => "1 KB – 4 KB",
                        3 => "4 KB – 64 KB",
                        4 => "64 KB – 1 MB",
                        _ => "≥ 1 MB",
                    };
                    return new[] { range, kv.Value.Count.ToString("N0"), DumpHelpers.FormatSize(kv.Value.Size) };
                })
                .ToList();
            int largeFreeCount = freeObjStats
                .Where(kv => kv.Key >= 4)
                .Sum(kv => (int)kv.Value.Count);
            sink.Table(["Free Hole Size", "Count", "Total Size"], freeRows,
                "Smaller/more-numerous holes = harder to compact");
            if (largeFreeCount > 10)
                sink.Alert(AlertLevel.Warning,
                    $"{largeFreeCount} free holes ≥ 64 KB — large gaps can be re-used by LOH allocations.",
                    "Large free holes often indicate recently freed large arrays or strings.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private struct SegInfo(string kind, ulong address, long committed)
    {
        public string Kind           = kind;
        public ulong  Address        = address;
        public long   CommittedBytes = committed;
        public long   LiveBytes;
        public long   FreeBytes;
        public int    PinnedCount;
    }
}
