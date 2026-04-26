using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Performs the detailed event-handler scan for <c>EventAnalysisAnalyzer.AnalyzeDetailed</c>.
/// Driven by <see cref="HeapWalker"/> for parallel segment processing.
/// Each clone accumulates into its own dictionaries; <see cref="MergeFrom"/> combines them.
/// </summary>
internal sealed class EventDetailConsumer : IHeapObjectConsumer
{
    private readonly HashSet<ulong>  _staticRoots;
    private readonly ClrRuntime      _runtime;
    private readonly Dictionary<ulong, string> _methodNameCache = new();

    // (Publisher type, field name) → subscriber list
    public readonly Dictionary<(string, string), List<EventSubscriberInfo>> RawGroups
        = new(128);

    // (Publisher type, field name) → distinct publisher-object count
    public readonly Dictionary<(string, string), int> InstanceCounts = new(128);

    // Number of unique publisher objects seen
    public int PublisherCount { get; private set; }

    // Running subscriber total — readable during the walk for live progress
    public long _subscriberCount;
    public ref long SubscriberCount => ref _subscriberCount;
    private long _objCount;

    public EventDetailConsumer(HashSet<ulong> staticRoots, ClrRuntime runtime)
    {
        _staticRoots = staticRoots;
        _runtime     = runtime;
    }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        // Track total objects scanned for live progress display even if filtered out.
        Interlocked.Increment(ref _objCount);

        // Skip objects with no delegate fields (pre-filtered by HeapWalker.BuildMeta)
        // and skip BCL / system types which are unlikely to have user event leaks.
        if (meta.DelegateFields.Length == 0) return;
        if (DumpHelpers.IsSystemType(meta.Name)) return;

        string typeName = meta.Name;

        foreach (var df in meta.DelegateFields)
        {
            string fn = df.Name;
            // Skip generic-sounding field names that are unlikely to be event backing fields.
            // Real event fields are named after the event (e.g. "_onCompleted", "Changed").
            if (fn is "action" or "callback" or "handler" or "func" or "del" or "delegate") continue;

            try
            {
                // Read the delegate object stored in this field.
                var delVal = df.Field.ReadObject(obj.Address, false);
                if (delVal.IsNull || !delVal.IsValid) continue;

                // Walk the delegate's _invocationList to find all subscribers.
                var subs = CollectSubscribers(delVal);
                if (subs.Count == 0) continue;

                PublisherCount++;
                // Interlocked because clones run in parallel and _subscriberCount
                // is read from outside for live progress display.
                Interlocked.Add(ref _subscriberCount, subs.Count);
                var key = (typeName, fn);
                if (!RawGroups.TryGetValue(key, out var list))
                    RawGroups[key] = list = new List<EventSubscriberInfo>();
                list.AddRange(subs);
                // Count distinct publisher objects per (type, field) key.
                ref int ic = ref CollectionsMarshal.GetValueRefOrAddDefault(InstanceCounts, key, out _);
                ic++;
            }
            catch { } // field read can fail on corrupted/partially-collected objects
        }
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone()
        => new EventDetailConsumer(_staticRoots, _runtime);

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (EventDetailConsumer)other;
        PublisherCount   += src.PublisherCount;
        _objCount        += src._objCount;        // MergeFrom is serial — no Interlocked needed
        _subscriberCount += src._subscriberCount;

        foreach (var (key, srcList) in src.RawGroups)
        {
            if (!RawGroups.TryGetValue(key, out var dst))
                RawGroups[key] = dst = new List<EventSubscriberInfo>(srcList.Count);
            dst.AddRange(srcList);
        }

        foreach (var (key, count) in src.InstanceCounts)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(InstanceCounts, key, out _);
            dst += count;
        }
    }

    // ── Subscriber collection (mirrors EventAnalysisAnalyzer helpers) ─────────

    private List<EventSubscriberInfo> CollectSubscribers(ClrObject del)
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
                    var sub = TryGetSub(item);
                    if (sub is not null) result.Add(sub);
                }
            }
            else
            {
                var sub = TryGetSub(del);
                if (sub is not null) result.Add(sub);
            }
        }
        catch { }
        return result;
    }

    private EventSubscriberInfo? TryGetSub(ClrObject del)
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
            string method = ResolveMethodName(del);
            return new EventSubscriberInfo(targetType, method, (long)target.Size,
                _staticRoots.Contains(target.Address), isLambda, target.Address);
        }
        catch { return null; }
    }

    private string ResolveMethodName(ClrObject del)
    {
        try
        {
            ulong ptr = del.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return "?";
            if (_methodNameCache.TryGetValue(ptr, out var cached))
                return cached;
            var m = _runtime.GetMethodByInstructionPointer(ptr);
            if (m is null)
            {
                var unresolved = $"0x{ptr:X}";
                _methodNameCache[ptr] = unresolved;
                return unresolved;
            }
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            var resolved = $"{typePart}{m.Name}";
            _methodNameCache[ptr] = resolved;
            return resolved;
        }
        catch { return "?"; }
    }
}
