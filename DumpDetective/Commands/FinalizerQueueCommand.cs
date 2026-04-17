using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Scans the heap's finalizer queue for objects awaiting Finalize(), reports per-type
// distribution across GC generations, flags critical-finalizer objects and resurrection
// candidates, and checks whether the finalizer thread itself is blocked.
internal static class FinalizerQueueCommand
{
    private const string Help = """
        Usage: DumpDetective finalizer-queue <dump-file> [options]

        Options:
          -n, --top <N>      Top N types (default: 30)
          -a, --addresses    Show up to 20 object addresses per type
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    // Per-type statistics accumulated during the finalizer queue scan.
    private sealed record TypeStats(
        int Count,         // total instances in the finalizer queue
        long Size,         // total byte size of those instances
        int Gen0, int Gen1, int Gen2, int Loh, int Poh,  // per-generation instance counts
        bool HasDispose,   // true when the type declares a Dispose() method
        bool IsCritical,   // true when the type derives from CriticalFinalizerObject / SafeHandle
        List<ulong> Addresses);  // up to 20 sample addresses when --addresses is active

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        int top = 30;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)
                int.TryParse(args[++i], out top);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int top = 30, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Finalizer Queue",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var (finThread, finFrames) = GetFinalizerThreadInfo(ctx);
        var stats     = ScanFinalizerQueue(ctx, showAddr);
        int  total    = stats.Values.Sum(v => v.Count);
        long totalSize = stats.Values.Sum(v => v.Size);
        int  critCount = stats.Values.Where(v => v.IsCritical).Sum(v => v.Count);
        int  gen2Loh  = stats.Values.Sum(v => v.Gen2 + v.Loh);

        sink.Section("Finalizer Queue Summary");
        if (total == 0) { sink.Text("Finalizer queue is empty — no finalizable objects found."); return; }

        RenderQueueKeyValues(sink, stats, total, totalSize, critCount, gen2Loh);
        RenderFinalizerThread(sink, finThread, finFrames);

        int resurrectionCandidates = CountResurrectionCandidates(ctx);
        RenderAdvisories(sink, total, gen2Loh, critCount, resurrectionCandidates);

        var sorted = stats.OrderByDescending(kv => kv.Value.Size).Take(top).ToList();
        RenderTypeTable(sink, sorted, top, stats.Count);

        if (showAddr)
            RenderAddresses(sink, sorted);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Finds the dedicated finalizer thread and captures up to 30 of its stack frames.
    // Returns null for the thread reference when the dump was taken before the finalizer
    // thread was started (e.g. very early in process lifetime).
    static (ClrThread? Thread, List<string> Frames) GetFinalizerThreadInfo(DumpContext ctx)
    {
        var finThread = ctx.Runtime.Threads.FirstOrDefault(t => t.IsFinalizer);
        var finFrames = finThread?.EnumerateStackTrace()
            .Select(f => f.FrameName ?? f.Method?.Signature ?? "")
            .Where(f => f.Length > 0)
            .Take(30)
            .ToList() ?? [];
        return (finThread, finFrames);
    }

    // Walks ClrHeap.EnumerateFinalizableObjects() and accumulates per-type TypeStats.
    // Method-table-keyed caches avoid re-walking the base-type chain for each instance.
    static Dictionary<string, TypeStats> ScanFinalizerQueue(DumpContext ctx, bool showAddr)
    {
        var stats        = new Dictionary<string, TypeStats>(StringComparer.Ordinal);
        var disposeCache = new Dictionary<ulong, bool>();  // MethodTable → has Dispose()
        var critCache    = new Dictionary<ulong, bool>();  // MethodTable → is CriticalFinalizer

        CommandBase.RunStatus("Reading finalizer queue...", () =>
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
                    Count      = e.Count + 1,
                    Size       = e.Size + size,
                    Gen0       = e.Gen0 + (gen == 0 ? 1 : 0),
                    Gen1       = e.Gen1 + (gen == 1 ? 1 : 0),
                    Gen2       = e.Gen2 + (gen == 2 ? 1 : 0),
                    Loh        = e.Loh  + (gen == 3 ? 1 : 0),
                    Poh        = e.Poh  + (gen == 4 ? 1 : 0),
                    HasDispose = e.HasDispose || hasDispose,
                    IsCritical = e.IsCritical || isCritical,
                };
            }
        });

        return stats;
    }

    // Counts finalizable objects that are also held by a strong or ref-counted GC handle.
    // Such objects will be "resurrected" — re-queued for finalization — which can cause
    // unbounded finalizer queue growth when the object re-registers itself in Finalize().
    static int CountResurrectionCandidates(DumpContext ctx)
    {
        var stronglyRooted = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.Strong or ClrHandleKind.RefCounted)
            .Select(h => h.Object.Address)
            .ToHashSet();

        return ctx.Heap.EnumerateFinalizableObjects()
            .Where(o => o.IsValid)
            .Count(o => stronglyRooted.Contains(o.Address));
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Overview key-values: totals and per-category counts.
    static void RenderQueueKeyValues(
        IRenderSink sink, Dictionary<string, TypeStats> stats,
        int total, long totalSize, int critCount, int gen2Loh)
    {
        sink.KeyValues([
            ("Total in queue",              total.ToString("N0")),
            ("Total size estimate",         DumpHelpers.FormatSize(totalSize)),
            ("Distinct types",              stats.Count.ToString("N0")),
            ("Types with Dispose()",        stats.Count(kv => kv.Value.HasDispose).ToString("N0")),
            ("Critical finalizer objects",  critCount.ToString("N0")),
            ("In Gen2 / LOH (most costly)", gen2Loh.ToString("N0")),
        ]);
    }

    // Finalizer thread state: managed/OS IDs, blocked detection, and the live call stack.
    // A blocked finalizer thread causes unbounded queue growth and eventual OOM.
    static void RenderFinalizerThread(IRenderSink sink, ClrThread? finThread, List<string> finFrames)
    {
        sink.Section("Finalizer Thread");
        if (finThread is null)
        {
            sink.Alert(AlertLevel.Warning, "Finalizer thread not found in this dump.");
            return;
        }

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
            sink.Table(["#", "Stack Frame"],
                finFrames.Select((f, i) => new[] { i.ToString(), f }).ToList(),
                "Finalizer thread call stack");
        else
            sink.Text("  (no managed frames — finalizer thread is idle or waiting for work)");
    }

    // Diagnostic advisories: queue-size thresholds, Gen2/LOH pressure, critical finalizers,
    // and resurrection candidates — ordered from most to least actionable.
    static void RenderAdvisories(
        IRenderSink sink, int total, int gen2Loh, int critCount, int resurrectionCandidates)
    {
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

        if (resurrectionCandidates > 0)
            sink.Alert(AlertLevel.Warning,
                $"{resurrectionCandidates} finalizable object(s) also have strong GC handles — possible resurrection.",
                "Objects that re-register for finalization in Finalize() create resurrection cycles.",
                "Avoid resurrecting objects in Finalize(). Prefer the Dispose pattern (IDisposable + GC.SuppressFinalize).");
    }

    // Main type table: top N types sorted by retained size with gen distribution and flags.
    static void RenderTypeTable(
        IRenderSink sink,
        List<KeyValuePair<string, TypeStats>> sorted,
        int top, int totalTypes)
    {
        sink.Section("Types by Queue Size");
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
            $"Top {rows.Count} of {totalTypes} types by size — ⚠N = N objects in Gen2/LOH");
    }

    // Per-type collapsible address panels (--addresses): up to 20 sample addresses per type
    // for follow-up inspection with WinDbg !dumpobj / !gcroot.
    static void RenderAddresses(IRenderSink sink, List<KeyValuePair<string, TypeStats>> sorted)
    {
        sink.Section("Object Addresses by Type");
        foreach (var (typeName, v) in sorted.Where(kv => kv.Value.Addresses.Count > 0))
        {
            sink.BeginDetails($"{typeName}  —  {v.Count:N0} total  (showing {v.Addresses.Count})", open: false);
            sink.Table(["Address"], v.Addresses.Select(a => new[] { $"0x{a:X16}" }).ToList());
            sink.EndDetails();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Maps a heap object's address to its GC generation via its containing segment.
    // Returns 0=Gen0, 1=Gen1, 2=Gen2, 3=LOH, 4=POH, -1=unknown.
    // Ephemeral segments are sub-divided by the generation range boundaries reported by ClrMD.
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

