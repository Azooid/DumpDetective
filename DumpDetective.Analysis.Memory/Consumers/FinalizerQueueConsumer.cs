using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Collects full per-type finalizer-queue statistics during the
/// <c>IFinalizableObjectConsumer</c> pass of <see cref="HeapWalker.Walk"/>.
///
/// <para>
/// Extracts the same per-type detail as <c>FinalizerQueueAnalyzer.ScanQueue</c>
/// (count, size, generation, HasDispose, IsCritical, sample addresses) in a single
/// sequential pass over <c>ClrHeap.EnumerateFinalizableObjects()</c>.
/// Resurrection-candidate detection (which needs <c>EnumerateHandles()</c>) is
/// intentionally left to the caller after the walk.
/// </para>
/// </summary>
public sealed class FinalizerQueueConsumer : IFinalizableObjectConsumer
{
    private readonly bool _collectAddresses;

    // Per-MethodTable caches — shared across the sequential pass, no locking needed.
    private readonly Dictionary<ulong, bool> _disposeCache = new();
    private readonly Dictionary<ulong, bool> _critCache    = new();
    private readonly Dictionary<string, MutableStat> _stats =
        new(256, StringComparer.Ordinal);

    // Addresses of every object in the queue — needed for resurrection check.
    private readonly HashSet<ulong> _finalizableAddrs = new();

    // Progress reporting — set before the pass starts.
    public Action<string>? Progress { get; set; }

    // ── Outputs (valid after OnFinalizableQueueComplete) ─────────────────────

    public IReadOnlyDictionary<string, FinalizerTypeStats>? Stats       { get; private set; }
    public IReadOnlySet<ulong>                              FinalizableAddresses => _finalizableAddrs;
    public int                                              Total        { get; private set; }
    public long                                             TotalSize    { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public FinalizerQueueConsumer(bool collectAddresses = false)
    {
        _collectAddresses = collectAddresses;
    }

    // ── IFinalizableObjectConsumer ────────────────────────────────────────────

    public void ConsumeFinalizableObject(in ClrObject obj, ClrHeap heap)
    {
        if (!obj.IsValid) return;
        _finalizableAddrs.Add(obj.Address);

        string typeName = obj.Type?.Name ?? "<unknown>";
        long   size     = (long)obj.Size;
        int    gen      = GetGen(heap, obj.Address);

        bool hasDispose = false, isCritical = false;
        if (obj.Type is not null)
        {
            if (!_disposeCache.TryGetValue(obj.Type.MethodTable, out hasDispose))
            {
                hasDispose = obj.Type.Methods.Any(m => m.Name == "Dispose");
                _disposeCache[obj.Type.MethodTable] = hasDispose;
            }
            if (!_critCache.TryGetValue(obj.Type.MethodTable, out isCritical))
            {
                var bt = obj.Type.BaseType;
                while (bt is not null)
                {
                    if (bt.Name is "System.Runtime.ConstrainedExecution.CriticalFinalizerObject"
                               or "System.Runtime.InteropServices.SafeHandle")
                    { isCritical = true; break; }
                    bt = bt.BaseType;
                }
                _critCache[obj.Type.MethodTable] = isCritical;
            }
        }

        if (!_stats.TryGetValue(typeName, out var e))
        {
            e = new MutableStat { HasDispose = hasDispose, IsCritical = isCritical };
            _stats[typeName] = e;
        }

        e.Count++;
        e.Size += size;
        if      (gen == 0) e.Gen0++;
        else if (gen == 1) e.Gen1++;
        else if (gen == 2) e.Gen2++;
        else if (gen == 3) e.Loh++;
        else if (gen == 4) e.Poh++;
        e.HasDispose |= hasDispose;
        e.IsCritical |= isCritical;
        if (_collectAddresses && e.Addresses.Count < 20) e.Addresses.Add(obj.Address);
    }

    public void OnFinalizableQueueComplete()
    {
        var result = new Dictionary<string, FinalizerTypeStats>(_stats.Count, StringComparer.Ordinal);
        int total     = 0;
        long totalSize = 0;
        foreach (var (typeName, m) in _stats)
        {
            result[typeName] = new FinalizerTypeStats(
                m.Count, m.Size, m.Gen0, m.Gen1, m.Gen2, m.Loh, m.Poh,
                m.HasDispose, m.IsCritical, m.Addresses);
            total     += m.Count;
            totalSize += m.Size;
        }
        Stats     = result;
        Total     = total;
        TotalSize = totalSize;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private sealed class MutableStat
    {
        public int         Count;
        public long        Size;
        public int         Gen0, Gen1, Gen2, Loh, Poh;
        public bool        HasDispose;
        public bool        IsCritical;
        public List<ulong> Addresses { get; } = [];
    }
}
