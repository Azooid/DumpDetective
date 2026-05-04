using Microsoft.Diagnostics.Runtime;
using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Shared heuristics for deciding whether a delegate-typed field is likely to be
/// a C# event backing field.  Reduces false positives that arise from treating every
/// delegate-typed field as a potential event leak.
/// </summary>
internal static class EventFieldFilter
{
    // Keyed by MethodTable (unique per type/assembly). Shared safely across parallel
    // HeapWalker clones — only written via GetOrAdd.
    private static readonly ConcurrentDictionary<ulong, HashSet<string>> s_eventNameCache = new();

    /// <summary>
    /// Returns true if the publisher type name is "noisy" — a compiler-generated
    /// or system-infrastructure type that is unlikely to own meaningful event backing fields.
    /// </summary>
    public static bool IsNoiseType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        if (typeName.IndexOf("System.Threading", StringComparison.Ordinal) >= 0) return true;
        if (typeName.IndexOf("System.Linq",      StringComparison.Ordinal) >= 0) return true;
        if (typeName.Contains("<>",          StringComparison.Ordinal)) return true;
        if (typeName.Contains("DisplayClass", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Returns true if the field name looks like a standard C# event backing field.
    /// Only call this after confirming the field type is a delegate type — the
    /// <c>StartsWith("_")</c> rule relies on that precondition.
    /// </summary>
    public static bool LooksLikeEventFieldName(string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return false;
        if (fieldName.IndexOf("Event",    StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.IndexOf("Changed",  StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.IndexOf("Handler",  StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.IndexOf("Raised",   StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.IndexOf("Fired",    StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (fieldName.Contains("k__BackingField", StringComparison.Ordinal)) return true;
        // Standard C# explicit event backing field: _clickEvent, _onChanged, etc.
        if (fieldName.StartsWith("_", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="fieldName"/> is likely an event backing field on
    /// <paramref name="publisherType"/>.  Uses <c>add_</c>/<c>remove_</c> method-pair
    /// introspection when available, then falls back to <see cref="LooksLikeEventFieldName"/>.
    /// </summary>
    public static bool IsLikelyEventField(ClrType? publisherType, string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return false;
        if (publisherType is null) return LooksLikeEventFieldName(fieldName);

        var eventNames = GetEventNames(publisherType);
        // Empty set means the type declares no own events → fall back to name heuristic.
        if (eventNames.Count == 0) return LooksLikeEventFieldName(fieldName);

        return eventNames.Contains(fieldName) || LooksLikeEventFieldName(fieldName);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static HashSet<string> GetEventNames(ClrType type)
    {
        ulong mt = type.MethodTable;
        if (s_eventNameCache.TryGetValue(mt, out var cached)) return cached;

        // Step 1: collect own add_X / remove_X pairs from the concrete type.
        var addNames    = new HashSet<string>(StringComparer.Ordinal);
        var removeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in type.Methods)
        {
            var name = method.Name;
            if (name is null) continue;
            if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                addNames.Add(name[4..]);
            else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                removeNames.Add(name[7..]);
        }

        var ownEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in addNames)
            if (removeNames.Contains(e)) ownEvents.Add(e);

        // Step 2: no own events → cache empty set, caller falls back to name heuristic.
        if (ownEvents.Count == 0)
            return s_eventNameCache.GetOrAdd(mt, ownEvents);

        // Step 3: walk base types so inherited backing fields are not lost.
        ClrType? current = type.BaseType;
        while (current is not null
            && current.Name != "System.Object"
            && current.Name != "System.Delegate"
            && current.Name != "System.MulticastDelegate")
        {
            foreach (var method in current.Methods)
            {
                var name = method.Name;
                if (name is null) continue;
                if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                    addNames.Add(name[4..]);
                else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                    removeNames.Add(name[7..]);
            }
            current = current.BaseType;
        }

        var allEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in addNames)
            if (removeNames.Contains(e)) allEvents.Add(e);

        return s_eventNameCache.GetOrAdd(mt, allEvents);
    }
}
