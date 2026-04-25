using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Analyzes the GC finalizer queue, reporting type-level counts and detecting
/// potential resurrection candidates.
/// Three phases:
///   1. Finalizer thread info: locates the finalizer thread in <c>ctx.Runtime.Threads</c>,
///      captures its stack frames, and checks whether it is currently blocked.
///   2. Queue scan: calls <c>ClrHeap.EnumerateFinalizableObjects</c> to iterate the
///      pending-finalization queue, grouping objects by type and optionally collecting
///      sample addresses. Checks each object for a finalizer method via reflection
///      (<c>GetMethod("Finalize")</c>) to distinguish true finalizable types from
///      objects promoted by resurrection.
///   3. Resurrection candidates: cross-references finalizableObjects against strong GC
///      handles — an object in the finalizer queue that is also strongly rooted is a
///      resurrection candidate (its finalizer will re-root it, keeping it alive).
/// </summary>
public sealed class FinalizerQueueAnalyzer
{
    public FinalizerQueueData Analyze(DumpContext ctx, bool collectAddresses = false)
    {
        var (finThread, finFrames, finBlocked) = GetFinalizerInfo(ctx);
        var stats      = ScanQueue(ctx, collectAddresses);
        int total      = stats.Values.Sum(v => v.Count);
        long totalSize = stats.Values.Sum(v => v.Size);

        // CountResurrectionCandidates re-enumerates finalizableObjects + handles;
        // wrapping in RunStatus makes this phase visible in the └─ trace.
        int resurrect = 0;
        CommandBase.RunStatus("Checking resurrection candidates...",
            () => resurrect = CountResurrectionCandidates(ctx));

        return new FinalizerQueueData(stats, total, totalSize, finBlocked, finFrames, resurrect,
            finThread?.ManagedThreadId ?? 0, finThread?.OSThreadId ?? 0);
    }

    private static (ClrThread? Thread, IReadOnlyList<string> Frames, bool Blocked)
        GetFinalizerInfo(DumpContext ctx)
    {
        var t = ctx.Runtime.Threads.FirstOrDefault(x => x.IsFinalizer);
        if (t is null) return (null, [], false);

        var frames = t.EnumerateStackTrace()
            .Select(f => f.FrameName ?? f.Method?.Signature ?? "")
            .Where(f => f.Length > 0)
            .Take(30)
            .ToList();

        bool blocked = frames.Any(f =>
            f.Contains("WaitForWork", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("WaitOne",     StringComparison.OrdinalIgnoreCase) ||
            f.Contains("Sleep",       StringComparison.OrdinalIgnoreCase));

        return (t, frames, blocked);
    }

    private static IReadOnlyDictionary<string, FinalizerTypeStats> ScanQueue(DumpContext ctx, bool collectAddresses)
    {
        var stats        = new Dictionary<string, FinalizerTypeStats>(StringComparer.Ordinal);
        var disposeCache = new Dictionary<ulong, bool>();
        var critCache    = new Dictionary<ulong, bool>();

        CommandBase.RunStatus("Reading finalizer queue...", update =>
        {
            int count = 0;
            var sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateFinalizableObjects())
            {
                if (!obj.IsValid) continue;
                count++;
                if ((count & 0xFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Reading finalizer queue \u2014 {count:N0} objects  \u2022  {stats.Count} types...");
                    sw.Restart();
                }
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
                    e = new FinalizerTypeStats(0, 0, 0, 0, 0, 0, 0, hasDispose, isCritical, new List<ulong>());

                var addrs = (List<ulong>)e.Addresses;
                if (collectAddresses && addrs.Count < 20) addrs.Add(obj.Address);

                stats[typeName] = new FinalizerTypeStats(
                    Count:      e.Count + 1,
                    Size:       e.Size + size,
                    Gen0:       e.Gen0 + (gen == 0 ? 1 : 0),
                    Gen1:       e.Gen1 + (gen == 1 ? 1 : 0),
                    Gen2:       e.Gen2 + (gen == 2 ? 1 : 0),
                    Loh:        e.Loh  + (gen == 3 ? 1 : 0),
                    Poh:        e.Poh  + (gen == 4 ? 1 : 0),
                    HasDispose: e.HasDispose || hasDispose,
                    IsCritical: e.IsCritical || isCritical,
                    Addresses:  addrs);
            }
        });

        return stats;
    }

    private static int CountResurrectionCandidates(DumpContext ctx)
    {
        try
        {
            // Objects with both a finalizer queue entry AND a non-weak handle are resurrection candidates
            var finalizableAddrs = ctx.Heap.EnumerateFinalizableObjects()
                .Select(o => o.Address).ToHashSet();
            return (int)ctx.Runtime.EnumerateHandles()
                .Count(h => h.HandleKind != ClrHandleKind.WeakShort
                         && h.HandleKind != ClrHandleKind.WeakLong
                         && finalizableAddrs.Contains(h.Object));
        }
        catch { return 0; }
    }

    private static int GetGen(ClrHeap heap, ulong addr)
    {
        var seg = heap.GetSegmentByAddress(addr);
        if (seg is null) return 2;
        return seg.Kind switch
        {
            GCSegmentKind.Generation0 => 0,
            GCSegmentKind.Generation1 => 1,
            GCSegmentKind.Generation2 => 2,
            GCSegmentKind.Large       => 3,
            GCSegmentKind.Pinned      => 4,
            GCSegmentKind.Ephemeral   =>
                seg.Generation0.Contains(addr) ? 0 :
                seg.Generation1.Contains(addr) ? 1 : 2,
            _ => 2,
        };
    }
}
