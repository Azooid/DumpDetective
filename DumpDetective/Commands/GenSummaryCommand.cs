using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class GenSummaryCommand
{
    private const string Help = """
        Usage: DumpDetective gen-summary <dump-file> [options]

        Options:
          -o, --output <f>   Write report to file
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
        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, frozen = 0;
        var segRows = new List<string[]>();

        foreach (var seg in ctx.Heap.Segments)
        {
            long committed = (long)seg.CommittedMemory.Length;
            string kind;
            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: gen0 += committed; kind = "Gen0"; break;
                case GCSegmentKind.Generation1: gen1 += committed; kind = "Gen1"; break;
                case GCSegmentKind.Generation2: gen2 += committed; kind = "Gen2"; break;
                case GCSegmentKind.Large:        loh  += committed; kind = "LOH";  break;
                case GCSegmentKind.Pinned:       poh  += committed; kind = "POH";  break;
                case GCSegmentKind.Frozen:       frozen += committed; kind = "Frozen"; break;
                case GCSegmentKind.Ephemeral:
                    gen0 += (long)seg.Generation0.Length;
                    gen1 += (long)seg.Generation1.Length;
                    gen2 += (long)seg.Generation2.Length;
                    kind = "Ephemeral"; break;
                default: kind = seg.Kind.ToString(); break;
            }
            segRows.Add([$"0x{seg.Address:X16}", kind, DumpHelpers.FormatSize(committed)]);
        }

        long total = gen0 + gen1 + gen2 + loh + poh + frozen;
        sink.Section("GC Generation Summary");
        sink.KeyValues([
            ("Gen0",   DumpHelpers.FormatSize(gen0)),
            ("Gen1",   DumpHelpers.FormatSize(gen1)),
            ("Gen2",   DumpHelpers.FormatSize(gen2)),
            ("LOH",    DumpHelpers.FormatSize(loh)),
            ("POH",    DumpHelpers.FormatSize(poh)),
            ("Frozen", DumpHelpers.FormatSize(frozen)),
            ("Total",  DumpHelpers.FormatSize(total)),
        ]);
        sink.Table(["Segment Address", "Kind", "Committed"], segRows, $"{segRows.Count} segment(s)");
    }
}
