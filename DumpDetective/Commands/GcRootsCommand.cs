using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class GcRootsCommand
{
    private const string Help = """
        Usage: DumpDetective gc-roots <dump-file> --type <typename> [options]

        Traces which GC roots keep instances of a type alive.
        Shows direct roots (static fields, thread stacks, GC handles) and 1-hop referrers.

        Options:
          -t, --type <name>       Type name to trace (case-insensitive)  [required]
          -n, --max-results <N>   Max instances to trace (default: 10)
          --no-indirect           Skip 1-hop referrer scan (faster on large dumps)
          -o, --output <f>        Write report to file
          -h, --help              Show this help
        """;

    private sealed record RootInfo(
        ClrRootKind Kind,
        string      KindLabel,
        ulong       RootAddress,
        int?        ThreadId);

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        string? typeName = null; int maxResults = 10; bool noIndirect = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--type" or "-t") && i + 1 < args.Length)           typeName   = args[++i];
            else if ((args[i] is "--max-results" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out maxResults);
            else if (args[i] == "--no-indirect")                                  noIndirect = true;
        }
        if (typeName is null) { AnsiConsole.MarkupLine("[bold red]✗[/] --type is required."); return 1; }
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, typeName, maxResults, noIndirect));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
                                string typeName, int maxResults = 10, bool noIndirect = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — GC Root Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete."); return; }

        var targets     = new List<ClrObject>();
        var directRoots = new Dictionary<ulong, List<RootInfo>>();
        var referrers   = new Dictionary<ulong, List<(ulong Addr, string Type)>>();
        bool capped     = false;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start($"Tracing roots of '{typeName}'...", status =>
            {
                // Step 1: find target instances
                var watch = Stopwatch.StartNew();
                long scanned = 0;
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    if (obj.Type.Name?.Contains(typeName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (targets.Count < maxResults) targets.Add(obj);
                        else { capped = true; break; }
                    }
                    scanned++;
                    if (watch.Elapsed.TotalSeconds >= 1)
                    {
                        status.Status($"Finding instances — {scanned:N0} objects scanned, {targets.Count} matches...");
                        watch.Restart();
                    }
                }
                if (targets.Count == 0) return;
                var targetSet = targets.Select(t => t.Address).ToHashSet();

                // Step 2: thread stack-range lookup for stack-root attribution
                var threadStacks = ctx.Runtime.Threads
                    .Where(t => t.IsAlive && t.StackBase != 0 && t.StackLimit != 0)
                    .Select(t => (
                        ThreadId: t.ManagedThreadId,
                        Lo: Math.Min(t.StackBase, t.StackLimit),
                        Hi: Math.Max(t.StackBase, t.StackLimit)))
                    .ToList();

                // Step 3: scan all GC roots for direct references to targets
                status.Status("Enumerating GC roots...");
                foreach (var root in ctx.Heap.EnumerateRoots())
                {
                    if (!targetSet.Contains(root.Object)) continue;
                    string kindLabel = root.RootKind switch
                    {
                        ClrRootKind.Stack             => "Stack (Local Variable)",
                        ClrRootKind.StrongHandle      => "Strong Handle",
                        ClrRootKind.PinnedHandle      => "Pinned Handle",
                        ClrRootKind.AsyncPinnedHandle => "Async-Pinned Handle",
                        ClrRootKind.RefCountedHandle  => "RefCount Handle",
                        ClrRootKind.FinalizerQueue    => "Finalizer Queue",
                        _                             => root.RootKind.ToString(),
                    };

                    // For stack roots: find owning thread by stack address range
                    int? threadId = null;
                    if (root.RootKind == ClrRootKind.Stack && root.Address != 0)
                    {
                        foreach (var (tid, lo, hi) in threadStacks)
                        {
                            if (root.Address >= lo && root.Address <= hi) { threadId = tid; break; }
                        }
                    }

                    var ri = new RootInfo(root.RootKind, kindLabel, root.Address, threadId);
                    if (!directRoots.TryGetValue(root.Object, out var list))
                        directRoots[root.Object] = list = [];
                    list.Add(ri);
                }

                // Step 4: 1-hop referrer map (skippable for large dumps)
                if (!noIndirect)
                {
                    status.Status("Building 1-hop referrer map...");
                    watch.Restart(); scanned = 0;
                    foreach (var obj in ctx.Heap.EnumerateObjects())
                    {
                        if (!obj.IsValid) continue;
                        foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                        {
                            if (!targetSet.Contains(refAddr)) continue;
                            if (!referrers.TryGetValue(refAddr, out var refList))
                                referrers[refAddr] = refList = [];
                            if (refList.Count < 50)
                                refList.Add((obj.Address, obj.Type?.Name ?? "?"));
                        }
                        scanned++;
                        if (watch.Elapsed.TotalSeconds >= 1)
                        {
                            status.Status($"Building referrer map — {scanned:N0} objects scanned...");
                            watch.Restart();
                        }
                    }
                }
            });

        // ── Summary ──────────────────────────────────────────────────────────
        sink.Section($"GC Roots: {typeName}");
        if (targets.Count == 0) { sink.Alert(AlertLevel.Info, $"No instances of '{typeName}' found on the heap."); return; }

        int rootedCount   = targets.Count(t => directRoots.TryGetValue(t.Address, out var r) && r.Count > 0);
        int orphanedCount = targets.Count - rootedCount;
        int totalRefs     = noIndirect ? 0 : referrers.Values.Sum(l => l.Count);

        sink.KeyValues([
            ("Matching instances",    targets.Count.ToString("N0") + (capped ? "  (capped — use -n to increase)" : "")),
            ("Directly GC-rooted",    rootedCount.ToString("N0")),
            ("Orphaned (no GC root)", orphanedCount.ToString("N0")),
            ("1-hop referrers",       noIndirect ? "skipped (--no-indirect)" : totalRefs.ToString("N0")),
        ]);

        if (orphanedCount > 0)
            sink.Alert(AlertLevel.Info,
                $"{orphanedCount} instance(s) appear unreachable — no direct GC roots.",
                "These objects will be collected on the next GC pass unless resurrected.",
                "If count is unexpectedly high, check for finalizer-queue resurrection patterns.");

        if (capped)
            sink.Alert(AlertLevel.Warning,
                $"Results capped at {maxResults} — more instances may exist.",
                advice: $"Re-run with a higher limit: -n 50");

        // ── Root kind summary ─────────────────────────────────────────────────
        var allRoots = directRoots.Values.SelectMany(l => l).ToList();
        if (allRoots.Count > 0)
        {
            var kindRows = allRoots
                .GroupBy(r => r.KindLabel)
                .OrderByDescending(g => g.Count())
                .Select(g => new[] { g.Key, g.Count().ToString("N0") })
                .ToList();
            sink.Table(["Root Kind", "Count"], kindRows, "Direct GC roots by kind");
        }

        // ── Per-instance root details ─────────────────────────────────────────
        sink.Section("Per-Instance Root Details");
        foreach (var target in targets)
        {
            directRoots.TryGetValue(target.Address, out var roots);
            referrers.TryGetValue(target.Address, out var refs);

            string gen = GetGenLabel(ctx, target.Address);
            string label = $"{target.Type?.Name ?? "?"}  [Gen {gen}]  @ 0x{target.Address:X16}  ({DumpHelpers.FormatSize((long)target.Size)})";

            bool rooted = roots is { Count: > 0 };
            sink.BeginDetails(label, open: rooted);

            if (rooted)
            {
                var rootRows = roots!.Select(r => new[]
                {
                    r.KindLabel,
                    r.RootAddress != 0 ? $"0x{r.RootAddress:X16}" : "—",
                    r.ThreadId.HasValue ? $"Thread {r.ThreadId}" : "—",
                }).ToList();
                sink.Table(["Root Kind", "Root Slot Address", "Thread"], rootRows, "Direct GC roots");
            }
            else
            {
                sink.Text("  No direct GC roots — object is orphaned (unreachable).");
            }

            if (!noIndirect && refs is { Count: > 0 })
            {
                var refRows = refs
                    .GroupBy(r => r.Type)
                    .OrderByDescending(g => g.Count())
                    .Take(15)
                    .Select(g => new[] { g.Key, g.Count().ToString("N0") })
                    .ToList();
                sink.Table(["Referrer Type", "Count"], refRows, $"1-hop referrers ({refs.Count} total, up to 50 tracked)");
            }
            sink.EndDetails();
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
}
