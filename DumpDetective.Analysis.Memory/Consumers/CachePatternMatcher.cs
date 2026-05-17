using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Shared heuristic used by <c>CachePatternsConsumer</c> and <c>CachePatternsAnalyzer</c>
/// to classify a CLR type as a cache-like accumulator.
///
/// Two-tier strategy:
/// <list type="number">
///   <item><b>Exact markers</b> — fast <c>Contains</c> tests for well-known BCL and
///   framework collection types (Dictionary, ConcurrentDictionary, MemoryCache, …).</item>
///   <item><b>PascalCase word heuristic</b> — extracts the simple class name (after the
///   last <c>.</c> or <c>+</c>) and checks whether the word "Cache" or "Pool" appears as
///   a PascalCase word component, i.e. the first character of the match is uppercase.
///   This catches any user-defined class like <c>EntityCacheManager</c>, <c>OrderCache</c>,
///   or <c>ProductBufferPool</c> without hardcoding specific names.</item>
/// </list>
/// </summary>
internal static class CachePatternMatcher
{
    internal static readonly (string Substring, string Kind)[] ExactMarkers =
    [
        ("ConcurrentDictionary`2", "ConcurrentDictionary"),
        ("Dictionary`2",           "Dictionary"),
        ("MemoryCache",            "MemoryCache"),
        ("ConcurrentBag`1",        "ConcurrentBag"),
        ("ConcurrentQueue`1",      "ConcurrentQueue"),
        ("LruCache",               "LruCache"),
        ("Cache`",                 "Custom Cache"),  // generic Cache<T>
        ("ObjectPool`",            "ObjectPool"),    // generic ObjectPool<T>
        ("ImmutableDictionary`2",  "ImmutableDictionary"),
        ("HashSet`1",              "HashSet"),
    ];

    /// <summary>
    /// Returns a non-null kind string when <paramref name="typeName"/> matches any
    /// exact marker OR the PascalCase word heuristic.  Returns <see langword="null"/>
    /// when the type is not cache-like.
    /// </summary>
    internal static string? Classify(string typeName)
    {
        // 1. Exact markers (BCL / framework types).
        foreach (var (marker, kind) in ExactMarkers)
        {
            if (typeName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        // 2. PascalCase word heuristic for user-defined types.
        //    Strip generic type arguments before extracting the simple class name.
        //    Without this, the CLR name "MyApp.EntityCacheManager`2[System.Int32,System.String]"
        //    makes LastIndexOf find the '.' inside "System.String", returning "String]" as the
        //    simple name and missing the "Cache" word entirely.
        int bracketIdx = typeName.IndexOf('[');
        string baseName = bracketIdx >= 0 ? typeName[..bracketIdx] : typeName;
        int sep = baseName.LastIndexOfAny(['.', '+']);
        string simpleName = sep >= 0 ? baseName[(sep + 1)..] : baseName;

        if (ContainsPascalWord(simpleName, "Cache")) return "Custom Cache";
        if (ContainsPascalWord(simpleName, "Pool"))  return "Object Pool";

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="word"/> appears inside
    /// <paramref name="name"/> with the first character of the match being uppercase —
    /// i.e., it is a PascalCase word boundary.
    ///
    /// Examples that match "Cache":
    ///   EntityCacheManager → 'C' at position 6 is uppercase ✓
    ///   FooCache           → 'C' at position 3 is uppercase ✓
    ///   CacheService       → 'C' at position 0 is uppercase ✓
    ///
    /// Examples that do NOT match "Cache":
    ///   uncacheable        → 'c' lowercase ✗
    ///   nocache            → 'c' lowercase ✗
    /// </summary>
    private static bool ContainsPascalWord(string name, string word)
    {
        int start = 0;
        while (start < name.Length)
        {
            int pos = name.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            // Only a real PascalCase word start if the matched char is uppercase.
            if (char.IsUpper(name[pos])) return true;
            start = pos + 1;
        }
        return false;
    }

    // ── Entry-count reading ───────────────────────────────────────────────────

    private static readonly string[] DirectCountFields =
        ["_count", "m_count", "_size", "count", "_entryCount", "_itemCount"];

    /// <summary>
    /// Reads the logical number of entries stored in <paramref name="obj"/>.
    ///
    /// <b>Pass 1 — direct fields</b>: probes well-known BCL integer count fields
    /// (<c>_count</c>, <c>m_count</c>, <c>_size</c>, …) directly on the object.
    /// This handles <c>Dictionary&lt;K,V&gt;</c>, <c>List&lt;T&gt;</c>, etc.
    ///
    /// <b>Pass 2 — nested collection fields</b>: if pass 1 returns 0, enumerates
    /// all reference-typed instance fields of the object and reads the entry count
    /// of any field whose type name looks like a BCL collection
    /// (<c>Dictionary</c>, <c>List`</c>, <c>ConcurrentDictionary</c>, <c>HashSet</c>, …).
    /// This handles user-defined cache managers that wrap a private
    /// <c>Dictionary&lt;K,V&gt;</c> or <c>ConcurrentDictionary&lt;K,V&gt;</c> field,
    /// e.g. <c>EntityCacheManager._cache</c>.
    /// Returns the maximum count found across all matching fields.
    /// </summary>
    internal static long ReadEntryCount(in ClrObject obj)
    {
        if (obj.Type is null) return 0;

        // Pass 1: direct integer count field on the object itself.
        long direct = ReadDirectCount(obj);
        if (direct > 0) return direct;

        // Pass 2: look for a nested BCL collection field and read its count.
        long maxNested = 0;
        try
        {
            foreach (var field in obj.Type.Fields)
            {
                if (field.ElementType != ClrElementType.Object) continue;
                ClrObject nested;
                try { nested = field.ReadObject(obj, interior: false); }
                catch { continue; }
                if (!nested.IsValid || nested.Type is null) continue;

                string? tn = nested.Type.Name;
                if (tn is null) continue;

                // Only inspect obvious BCL collection types to avoid reading every field.
                if (!tn.Contains("Dictionary", StringComparison.Ordinal) &&
                    !tn.Contains("List`",       StringComparison.Ordinal) &&
                    !tn.Contains("HashSet`",    StringComparison.Ordinal) &&
                    !tn.Contains("Queue`",      StringComparison.Ordinal) &&
                    !tn.Contains("Stack`",      StringComparison.Ordinal) &&
                    !tn.Contains("Bag`",        StringComparison.Ordinal)) continue;

                long c = ReadDirectCount(nested);
                if (c > maxNested) maxNested = c;
            }
        }
        catch { }

        return maxNested;
    }

    /// <summary>Reads a direct integer count/size field from <paramref name="obj"/>.</summary>
    internal static long ReadDirectCount(in ClrObject obj)
    {
        if (obj.Type is null) return 0;
        foreach (var fn in DirectCountFields)
        {
            var field = obj.Type.GetFieldByName(fn);
            if (field is null) continue;
            try
            {
                if (field.ElementType is ClrElementType.Int32)
                    return field.Read<int>(obj, interior: false);
                if (field.ElementType is ClrElementType.Int64)
                    return field.Read<long>(obj, interior: false);
            }
            catch { }
        }
        return 0;
    }
}
