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
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var segData = new Dictionary<ulong, (string Kind, long Live, long Free, long Committed)>();
        foreach (var seg in ctx.Heap.Segments)
            segData[seg.Address] = (DumpHelpers.SegmentKindLabel(ctx.Heap, seg.Address), 0, 0, (long)seg.CommittedMemory.Length);

        var freeType = ctx.Heap.FreeType;
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Measuring fragmentation...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;
                var seg = ctx.Heap.GetSegmentByAddress(obj.Address);
                if (seg is null || !segData.ContainsKey(seg.Address)) continue;
                var (kind, live, free, committed) = segData[seg.Address];
                long size = (long)obj.Size;
                if (obj.Type == freeType) segData[seg.Address] = (kind, live, free + size, committed);
                else                      segData[seg.Address] = (kind, live + size, free, committed);
            }
        });

        var rows = segData.Values
            .Where(s => s.Committed > 0)
            .OrderByDescending(s => s.Committed > 0 ? s.Free * 100.0 / s.Committed : 0)
            .Select(s => {
                double frag = s.Committed > 0 ? s.Free * 100.0 / s.Committed : 0;
                return new[] { s.Kind, DumpHelpers.FormatSize(s.Committed), DumpHelpers.FormatSize(s.Live), DumpHelpers.FormatSize(s.Free), $"{frag:F1}%" };
            }).ToList();

        long totalCommitted = segData.Values.Sum(s => s.Committed);
        long totalFree      = segData.Values.Sum(s => s.Free);
        double totalFrag    = totalCommitted > 0 ? totalFree * 100.0 / totalCommitted : 0;

        sink.Section("Heap Fragmentation");
        sink.Table(["Segment", "Committed", "Live", "Free", "Frag %"], rows);
        sink.KeyValues([
            ("Total committed",    DumpHelpers.FormatSize(totalCommitted)),
            ("Total free space",   DumpHelpers.FormatSize(totalFree)),
            ("Overall frag %",     $"{totalFrag:F1}%"),
        ]);

        if (totalFrag >= 40)
            sink.Alert(AlertLevel.Critical, $"Heap fragmentation is high: {totalFrag:F1}%",
                advice: "Reduce pinned handles. Use MemoryPool<T> for I/O buffers.");
        else if (totalFrag >= 20)
            sink.Alert(AlertLevel.Warning, $"Heap fragmentation: {totalFrag:F1}%");
    }
}
