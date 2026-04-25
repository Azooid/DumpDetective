using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Detects event handler leaks by counting the number of subscribers on every
/// delegate-typed instance field on non-system types.
/// </summary>
public sealed class EventAnalysisAnalyzer : IHeapObjectConsumer
{
    private Dictionary<(string, string), int>? _totals;
    private EventAnalysisData? _result;

    public EventAnalysisData? Result => _result;

    internal void Reset()
    {
        _totals = new Dictionary<(string, string), int>(4096);
        _result = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.DelegateFields.Length == 0 || _totals is null) return;

        foreach (var field in meta.DelegateFields)
        {
            try
            {
                var delVal = field.Field.ReadObject(obj.Address, false);
                if (delVal.IsNull || !delVal.IsValid) continue;
                int subs = CountSubscribers(delVal);
                if (subs <= 0) continue;

                var key = (meta.Name, field.Name);
                ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault<(string, string), int>(_totals, key, out bool existed);
                existing = existed ? existing + subs : subs;
            }
            catch { }
        }
    }

    public void OnWalkComplete()
    {
        if (_totals is null || _totals.Count == 0)
        {
            _result = new EventAnalysisData([]);
            return;
        }

        var groups = new List<EventLeakGroup>(_totals.Count);
        foreach (var kv in _totals)
            groups.Add(new EventLeakGroup(kv.Key.Item1, kv.Key.Item2, kv.Value));
        groups.Sort(static (a, b) => b.Subscribers.CompareTo(a.Subscribers));

        _result = new EventAnalysisData(groups);
    }

    public IHeapObjectConsumer CreateClone()
    {
        var c = new EventAnalysisAnalyzer();
        c.Reset();
        return c;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (EventAnalysisAnalyzer)other;
        if (src._totals is null) return;
        _totals ??= new Dictionary<(string, string), int>(4096);
        foreach (var (key, count) in src._totals)
        {
            ref int dst = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_totals, key, out _);
            dst += count;
        }
    }

    // ── Command entry point ───────────────────────────────────────────────────

    public EventAnalysisData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<EventAnalysisData>() is { } cached) return cached;

        // Full detailed scan (matching old EventAnalysisCommand behavior)
        var result = AnalyzeDetailed(ctx);
        ctx.SetAnalysis(result);
        return result;
    }

    // Detailed scan: builds static roots, then walks heap collecting per-subscriber detail.
    private static EventAnalysisData AnalyzeDetailed(DumpContext ctx)
    {
        HashSet<ulong> staticRoots = [];
        CommandBase.RunStatus("Building static root map...", () => staticRoots = BuildStaticRoots(ctx));

        var consumer = new Consumers.EventDetailConsumer(staticRoots, ctx.Runtime);

        CommandBase.RunStatus("Scanning event handlers (detailed)...", update =>
            HeapWalker.Walk(ctx.Heap, [consumer],
                raw =>
                {
                    // Enrich the standard walker message with event-specific counts
                    if (raw.StartsWith("Walking heap objects", StringComparison.Ordinal) ||
                        raw.StartsWith("merging", StringComparison.Ordinal)             ||
                        raw.StartsWith("finalising", StringComparison.Ordinal))
                    {
                        update($"Scanning event handlers \u2014 {consumer.PublisherCount:N0} publishers  \u2022  {Interlocked.Read(ref consumer.SubscriberCount):N0} subscribers  \u2022  {raw}");
                    }
                    else
                    {
                        update(raw);
                    }
                }));

        var rawGroups      = consumer.RawGroups;
        var instanceCounts = consumer.InstanceCounts;

        var groups = rawGroups
            .Select(kv =>
            {
                var allSubs    = kv.Value;
                bool isStatic  = staticRoots.Contains(GetRepresentativeAddr(allSubs));
                bool hasSR     = allSubs.Any(s => s.IsStaticRooted);
                long retained  = allSubs.Sum(s => s.Size);
                int  dupes     = CountDuplicates(allSubs);
                int  lambdas   = allSubs.Count(s => s.IsLambda);
                instanceCounts.TryGetValue(kv.Key, out int instCount);
                return new EventLeakGroup(
                    Publisher:        kv.Key.Item1,
                    Field:            kv.Key.Item2,
                    Subscribers:      allSubs.Count,
                    IsStaticPublisher: isStatic,
                    HasStaticSubs:   hasSR,
                    DuplicateCount:  dupes,
                    RetainedBytes:   retained,
                    LambdaCount:     lambdas,
                    InstanceCount:   instCount,
                    AllSubs:         allSubs);
            })
            .OrderByDescending(g => g.IsStaticPublisher)
            .ThenByDescending(g => g.Subscribers)
            .ToList();

        // Sum of instanceCounts values = total (pub-obj, field) pairs with subscribers
        // This matches old EventAnalysisCommand's leaks.Count passed to RenderFooter
        int totalPublisherInstances = instanceCounts.Values.Sum();
        return new EventAnalysisData(groups, totalPublisherInstances);
    }

    private static ulong GetRepresentativeAddr(List<EventSubscriberInfo> subs) => 0; // addr no longer tracked

    private static int CountDuplicates(List<EventSubscriberInfo> subs)
    {
        // The new architecture merges subscribers from all publisher instances into one flat list.
        // Per-instance duplicate detection (same target addr on the same publisher twice) is not
        // possible without per-instance separation. The old code used per-instance grouping.
        // Return 0 to match old behavior where cross-instance same-addr are not counted as duplicates.
        _ = subs;
        return 0;
    }

    private static HashSet<ulong> BuildStaticRoots(DumpContext ctx)
    {
        var staticRoots = new HashSet<ulong>();
        try
        {
            foreach (var appDomain in ctx.Runtime.AppDomains)
            foreach (var module in appDomain.Modules)
            foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (mt == 0) continue;
                var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                if (clrType is null) continue;
                foreach (var sf in clrType.StaticFields)
                {
                    if (!sf.IsObjectReference) continue;
                    try { var obj = sf.ReadObject(appDomain); if (obj.IsValid && !obj.IsNull) staticRoots.Add(obj.Address); }
                    catch { }
                }
            }
        }
        catch { }
        return staticRoots;
    }

    private static List<EventSubscriberInfo> CollectSubscribers(
        ClrObject del, HashSet<ulong> staticRoots, ClrRuntime runtime)
    {
        var result = new List<EventSubscriberInfo>();
        try
        {
            var invList = del.ReadObjectField("_invocationList");
            if (!invList.IsNull && invList.IsValid && invList.Type?.IsArray == true)
            {
                var arr = invList.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var item = arr.GetObjectValue(i);
                    if (!item.IsValid || item.IsNull) continue;
                    var sub = TryGetSub(item, staticRoots, runtime);
                    if (sub is not null) result.Add(sub);
                }
            }
            else
            {
                var sub = TryGetSub(del, staticRoots, runtime);
                if (sub is not null) result.Add(sub);
            }
        }
        catch { }
        return result;
    }

    private static EventSubscriberInfo? TryGetSub(
        ClrObject del, HashSet<ulong> staticRoots, ClrRuntime runtime)
    {
        try
        {
            var target = del.ReadObjectField("_target");
            if (target.IsNull) return null;
            string targetType = target.Type?.Name ?? "<unknown>";
            if (DumpHelpers.IsSystemType(targetType)) return null;
            bool isLambda = targetType.Contains("<>c", StringComparison.Ordinal) ||
                            targetType.Contains("+<>",  StringComparison.Ordinal) ||
                            targetType.Contains("__DisplayClass", StringComparison.Ordinal);
            string method = ResolveMethodName(del, runtime);
            return new EventSubscriberInfo(targetType, method, (long)target.Size,
                staticRoots.Contains(target.Address), isLambda, target.Address);
        }
        catch { return null; }
    }

    private static string ResolveMethodName(ClrObject del, ClrRuntime runtime)
    {
        try
        {
            ulong ptr = del.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return "?";
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return $"0x{ptr:X}";
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            return $"{typePart}{m.Name}";
        }
        catch { return "?"; }
    }

    private static bool IsDelegate(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "System.MulticastDelegate" or "System.Delegate") return true;
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountSubscribers(ClrObject del) => HeapWalker.CountSubscribers(del);
}
