using System.Reflection;
using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Thin wrappers around <see cref="TraceEvent"/> payload access.
/// All methods return a safe default (empty string / 0) when the field is
/// missing, null, or the conversion fails — preventing hot-path exceptions.
/// </summary>
internal static class TraceEventHelpers
{
    internal static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    internal static int SafeInt(TraceEvent ev, string field)
    {
        try { return (int)Convert.ChangeType(ev.PayloadByName(field), typeof(int)); } catch { return 0; }
    }

    internal static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0L; }
    }

    internal static double SafeDouble(TraceEvent ev, string field)
    {
        try { return (double)Convert.ChangeType(ev.PayloadByName(field), typeof(double)); } catch { return 0d; }
    }

    /// <summary>
    /// Tries each field name in order, returning the first non-null int conversion.
    /// Returns 0 if all fields fail.
    /// </summary>
    internal static int SafeInt(TraceEvent ev, params string[] fields)
    {
        foreach (var field in fields)
        {
            try
            {
                var v = ev.PayloadByName(field);
                if (v is not null) return (int)Convert.ChangeType(v, typeof(int));
            }
            catch { }
        }
        return 0;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1 << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1 << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1 << 10):F1} KB";
        return $"{bytes} B";
    }

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// GCAllocationTick fires roughly every 100 KB allocated.
    /// Used as a fallback when the event's AllocationAmount payload is zero.
    /// </summary>
    internal const long GcAllocTickSizeBytes = 100_000;

    /// <summary>
    /// Converts a sparse per-second bucket dictionary into a dense timeline array.
    /// Returns <see langword="null"/> when there are fewer than two data points.
    /// </summary>
    internal static double[]? BuildTimeline(Dictionary<int, int> buckets)
    {
        if (buckets.Count <= 1) return null;
        int minB = buckets.Keys.Min(), maxB = buckets.Keys.Max();
        var tl = new double[maxB - minB + 1];
        foreach (var kv in buckets) tl[kv.Key - minB] = kv.Value;
        return tl;
    }

    /// <inheritdoc cref="BuildTimeline(Dictionary{int,int})"/>
    internal static double[]? BuildTimeline(Dictionary<int, double> buckets)
    {
        if (buckets.Count <= 1) return null;
        int minB = buckets.Keys.Min(), maxB = buckets.Keys.Max();
        var tl = new double[maxB - minB + 1];
        foreach (var kv in buckets) tl[kv.Key - minB] = kv.Value;
        return tl;
    }

    /// <summary>
    /// Extracts the value of a named key from a DiagnosticSource Arguments StructValue[].
    /// DiagnosticSource ETW bridge stores Arguments as an array of StructValue objects —
    /// each with a String indexer: <c>item["Key"]</c> → field name, <c>item["Value"]</c> → value.
    /// Returns <see langword="null"/> if the field is absent or if Arguments is not a StructValue[].
    /// </summary>
    internal static string? ExtractStructValueField(TraceEvent ev, string fieldName)
    {
        object? argsObj;
        try { argsObj = ev.PayloadByName("Arguments"); }
        catch { return null; }
        if (argsObj is not System.Collections.IEnumerable enumerable) return null;

        MethodInfo? getItem = null;
        foreach (var item in enumerable)
        {
            if (item is null) continue;
            if (getItem is null)
            {
                getItem = item.GetType().GetMethod("get_Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                if (getItem is null) return null; // not a StructValue, bail
            }
            try
            {
                string? k = getItem.Invoke(item, s_keyArg) as string;
                if (string.Equals(k, fieldName, StringComparison.OrdinalIgnoreCase))
                    return getItem.Invoke(item, s_valueArg) as string;
            }
            catch { /* skip malformed entry */ }
        }
        return null;
    }

    // Reusable arg arrays to avoid per-call allocation in ExtractStructValueField.
    private static readonly object[] s_keyArg   = { "Key" };
    private static readonly object[] s_valueArg = { "Value" };

    /// <summary>
    /// Extracts a value from the DiagnosticSource Arguments string format:
    /// <c>[Key->"value",Key2->"value2",...]</c>
    /// Returns <see langword="null"/> if the key is absent.
    /// </summary>
    internal static string? ExtractArgValue(string arguments, string key)
    {
        string needle = key + "->\"";
        int idx = arguments.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int start = idx + needle.Length;
        if (start >= arguments.Length) return null;
        int end = arguments.IndexOf('"', start);
        if (end < 0) return null;
        return arguments[start..end];
    }
}
