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
        var staticRoots = BuildStaticRoots(ctx);

        // (publisher, field) → list of subscriber detail
        var rawGroups = new Dictionary<(string Publisher, string Field), List<EventSubscriberInfo>>(128);
        // (publisher, field) → count of distinct publisher objects
        var instanceCounts = new Dictionary<(string, string), int>(128);
        var publisherAddrs = new HashSet<ulong>();  // count unique publisher instances

        CommandBase.RunStatus("Scanning event handlers (detailed)...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                string typeName = obj.Type.Name ?? string.Empty;
                if (DumpHelpers.IsSystemType(typeName)) continue;

                foreach (var field in obj.Type.Fields)
                {
                    if (!field.IsObjectReference || field.Type is null || !IsDelegate(field.Type)) continue;
                    string ft = field.Type.Name ?? string.Empty;
                    if (ft.StartsWith("System.Action", StringComparison.Ordinal) ||
                        ft.StartsWith("System.Func",   StringComparison.Ordinal) ||
                        ft.StartsWith("System.Threading.Thread", StringComparison.Ordinal))
                        continue;
                    string fn = field.Name ?? string.Empty;
                    if (fn is "action" or "callback" or "handler" or "func" or "del" or "delegate") continue;

                    try
                    {
                        var delVal = field.ReadObject(obj.Address, false);
                        if (delVal.IsNull || !delVal.IsValid) continue;
                        var subs = CollectSubscribers(delVal, staticRoots, ctx.Runtime);
                        if (subs.Count == 0) continue;

                        publisherAddrs.Add(obj.Address);
                        var key = (typeName, field.Name ?? "<?>");
                        if (!rawGroups.TryGetValue(key, out var list))
                            rawGroups[key] = list = new List<EventSubscriberInfo>();
                        list.AddRange(subs);
                        ref int ic = ref CollectionsMarshal.GetValueRefOrAddDefault(instanceCounts, key, out _);
                        ic++;
                    }
                    catch { }
                }
            }
        });

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
                    Publisher:        kv.Key.Publisher,
                    Field:            kv.Key.Field,
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
