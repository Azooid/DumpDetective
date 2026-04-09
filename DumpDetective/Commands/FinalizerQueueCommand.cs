using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class FinalizerQueueCommand
{
    private const string Help = """
        Usage: DumpDetective finalizer-queue <dump-file> [options]

        Options:
          -n, --top <N>      Top N types (default: 30)
          -a, --addresses    Show up to 20 object addresses per type
          -o, --output <f>   Write report to file
          -h, --help         Show this help
        """;

    private sealed record TypeStats(
        int Count, long Size,
        int Gen0, int Gen1, int Gen2, int Loh, int Poh,
        bool HasDispose, bool IsCritical,
        List<ulong> Addresses);

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        int top = 30;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 30, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Finalizer Queue",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // ── Phase 1: finalizer thread status ──────────────────────────────────
        var finThread = ctx.Runtime.Threads.FirstOrDefault(t => t.IsFinalizer);
        var finFrames = finThread?.EnumerateStackTrace()
            .Select(f => f.FrameName ?? f.Method?.Signature ?? "")
            .Where(f => f.Length > 0)
            .Take(30)
            .ToList() ?? [];

        // ── Phase 2: scan finalizable objects ─────────────────────────────────
        var stats        = new Dictionary<string, TypeStats>(StringComparer.Ordinal);
        var disposeCache = new Dictionary<ulong, bool>();
        var critCache    = new Dictionary<ulong, bool>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Reading finalizer queue...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateFinalizableObjects())
            {
                if (!obj.IsValid) continue;
                string typeName = obj.Type?.Name ?? "<unknown>";
                long   size     = (long)obj.Size;
                int    gen      = GetGen(ctx.Heap, obj.Address);

                bool hasDispose = false, isCritical = false;
                if (obj.Type is not null)
                {
                    if (!disposeCache.TryGetValue(obj.Type.MethodTable, out hasDispose))
                    {
                        hasDispose = obj.Type.Methods.Any(m => m.Name == "Dispose");
                        disposeCache[obj.Type.MethodTable] = hasDispose;
                    }
                    if (!critCache.TryGetValue(obj.Type.MethodTable, out isCritical))
                    {
                        // Walk base type chain looking for CriticalFinalizerObject
                        var bt = obj.Type.BaseType;
                        while (bt is not null)
                        {
                            if (bt.Name is "System.Runtime.ConstrainedExecution.CriticalFinalizerObject"
                                       or "System.Runtime.InteropServices.SafeHandle")
                            { isCritical = true; break; }
                            bt = bt.BaseType;
                        }
                        critCache[obj.Type.MethodTable] = isCritical;
                    }
                }

                if (!stats.TryGetValue(typeName, out var e))
                    e = new TypeStats(0, 0, 0, 0, 0, 0, 0, hasDispose, isCritical, []);

                var addrs = e.Addresses;
                if (showAddr && addrs.Count < 20) addrs.Add(obj.Address);

                stats[typeName] = e with
                {
                    Count    = e.Count + 1,
                    Size     = e.Size + size,
                    Gen0     = e.Gen0 + (gen == 0 ? 1 : 0),
                    Gen1     = e.Gen1 + (gen == 1 ? 1 : 0),
                    Gen2     = e.Gen2 + (gen == 2 ? 1 : 0),
                    Loh      = e.Loh  + (gen == 3 ? 1 : 0),
                    Poh      = e.Poh  + (gen == 4 ? 1 : 0),
                    HasDispose = e.HasDispose || hasDispose,
                    IsCritical = e.IsCritical || isCritical,
                };
            }
        });

        int  total       = stats.Values.Sum(v => v.Count);
        long totalSize   = stats.Values.Sum(v => v.Size);
        int  critCount   = stats.Values.Where(v => v.IsCritical).Sum(v => v.Count);
        int  gen2Loh     = stats.Values.Sum(v => v.Gen2 + v.Loh);

        // ── Summary ───────────────────────────────────────────────────────────
        sink.Section("Finalizer Queue Summary");
        if (total == 0) { sink.Text("Finalizer queue is empty — no finalizable objects found."); return; }

        sink.KeyValues([
            ("Total in queue",              total.ToString("N0")),
            ("Total size estimate",         DumpHelpers.FormatSize(totalSize)),
            ("Distinct types",              stats.Count.ToString("N0")),
            ("Types with Dispose()",        stats.Count(kv => kv.Value.HasDispose).ToString("N0")),
            ("Critical finalizer objects",  critCount.ToString("N0")),
            ("In Gen2 / LOH (most costly)", gen2Loh.ToString("N0")),
        ]);

        // ── Finalizer thread status ───────────────────────────────────────────
        sink.Section("Finalizer Thread");
        if (finThread is null)
        {
            sink.Alert(AlertLevel.Warning, "Finalizer thread not found in this dump.");
        }
        else
        {
            bool isBlocked = finFrames.Any(f =>
                f.Contains("WaitOne",    StringComparison.OrdinalIgnoreCase) ||
                f.Contains("WaitHandle", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Monitor",    StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Sleep",      StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Join",       StringComparison.OrdinalIgnoreCase));

            sink.KeyValues([
                ("Managed thread ID", finThread.ManagedThreadId.ToString()),
                ("OS thread ID",      $"0x{finThread.OSThreadId:X}"),
                ("State",             isBlocked ? "⚠ BLOCKED" : (finFrames.Count > 0 ? "Running" : "Idle")),
            ]);

            if (isBlocked)
                sink.Alert(AlertLevel.Critical,
                    "Finalizer thread appears blocked.",
                    "A blocked finalizer thread causes the queue to grow without bound, leading to OOM.",
                    "Check the call stack below — avoid I/O, locks, or any blocking calls inside Finalize().");

            if (finFrames.Count > 0)
            {
                var frameRows = finFrames
                    .Select((f, i) => new[] { i.ToString(), f })
                    .ToList();
                sink.Table(["#", "Stack Frame"], frameRows, "Finalizer thread call stack");
            }
            else
            {
                sink.Text("  (no managed frames — finalizer thread is idle or waiting for work)");
            }
        }

        // ── Advisories ────────────────────────────────────────────────────────
        sink.Alert(AlertLevel.Info,
            "All objects in the finalizer queue delay GC collection of their entire retained object graph.",
            advice: "Call Dispose() / use 'using' statements to avoid finalizer pressure. Finalizers run on a single dedicated thread.");

        if (total >= 500)
            sink.Alert(AlertLevel.Critical, $"{total:N0} objects pending finalization.",
                advice: "A large finalizer queue indicates heavy GC pressure. Wrap IDisposable objects in 'using'.");
        else if (total >= 100)
            sink.Alert(AlertLevel.Warning, $"{total:N0} objects pending finalization.");

        if (gen2Loh > 0)
            sink.Alert(AlertLevel.Warning,
                $"{gen2Loh:N0} finalizable objects are in Gen2/LOH.",
                "Gen2/LOH objects survived at least 2 GC cycles before landing in the queue.",
                "These cause the most GC overhead — they block segment reclaim until Finalize() completes.");

        if (critCount > 0)
            sink.Alert(AlertLevel.Info,
                $"{critCount:N0} critical finalizer objects (SafeHandle / CriticalFinalizerObject).",
                "These are prioritised by the GC but still block native handle release.",
                "Dispose() SafeHandles explicitly — do not rely on finalization for unmanaged resources.");

        // ── Resurrection detection ────────────────────────────────────────────
        var stronglyRooted = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.Strong or ClrHandleKind.RefCounted)
            .Select(h => h.Object.Address)
            .ToHashSet();

        var finalizableAddrs = new HashSet<ulong>(
            ctx.Heap.EnumerateFinalizableObjects()
                .Where(o => o.IsValid)
                .Select(o => o.Address));

        int resurrectionCandidates = finalizableAddrs.Count(a => stronglyRooted.Contains(a));
        if (resurrectionCandidates > 0)
            sink.Alert(AlertLevel.Warning,
                $"{resurrectionCandidates} finalizable object(s) also have strong GC handles — possible resurrection.",
                "Objects that re-register for finalization in Finalize() create resurrection cycles.",
                "Avoid resurrecting objects in Finalize(). Prefer the Dispose pattern (IDisposable + GC.SuppressFinalize).");

        // ── Type table ────────────────────────────────────────────────────────
        sink.Section("Types by Queue Size");
        var sorted = stats
            .OrderByDescending(kv => kv.Value.Size)
            .Take(top)
            .ToList();

        var rows = sorted.Select(kv =>
        {
            var v = kv.Value;
            long avg = v.Count > 0 ? v.Size / v.Count : 0;
            string gen2flag = (v.Gen2 + v.Loh) > 0 ? $" ⚠{v.Gen2 + v.Loh}" : "";
            return new[]
            {
                kv.Key,
                v.Count.ToString("N0"),
                DumpHelpers.FormatSize(v.Size),
                DumpHelpers.FormatSize(avg),
                $"G0:{v.Gen0} G1:{v.Gen1} G2:{v.Gen2} LOH:{v.Loh}" + gen2flag,
                v.HasDispose  ? "✓" : "—",
                v.IsCritical  ? "✓" : "—",
            };
        }).ToList();

        sink.Table(
            ["Type", "Count", "Total Size", "Avg Size", "Gen Distribution", "IDisposable", "Critical"],
            rows,
            $"Top {rows.Count} types by size — ⚠N = N objects in Gen2/LOH");

        // ── Per-type address listing ──────────────────────────────────────────
        if (showAddr)
        {
            sink.Section("Object Addresses by Type");
            foreach (var (typeName, v) in sorted.Where(kv => kv.Value.Addresses.Count > 0))
            {
                sink.BeginDetails($"{typeName}  —  {v.Count:N0} total  (showing {v.Addresses.Count})", open: false);
                var addrRows = v.Addresses
                    .Select(a => new[] { $"0x{a:X16}" })
                    .ToList();
                sink.Table(["Address"], addrRows);
                sink.EndDetails();
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns 0=Gen0, 1=Gen1, 2=Gen2, 3=LOH, 4=POH, -1=unknown
    static int GetGen(ClrHeap heap, ulong addr)
    {
        var seg = heap.GetSegmentByAddress(addr);
        if (seg is null) return -1;
        return seg.Kind switch
        {
            GCSegmentKind.Generation0 => 0,
            GCSegmentKind.Generation1 => 1,
            GCSegmentKind.Generation2 => 2,
            GCSegmentKind.Large       => 3,
            GCSegmentKind.Pinned      => 4,
            GCSegmentKind.Frozen      => 4,
            GCSegmentKind.Ephemeral   =>
                seg.Generation0.Contains(addr) ? 0 :
                seg.Generation1.Contains(addr) ? 1 : 2,
            _ => -1,
        };
    }
}

