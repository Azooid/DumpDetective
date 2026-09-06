using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Turns a "nearest ← next ← ... ← furthest" call-chain breadcrumb (as produced by
/// <c>ChromeTraceParser.BuildCallChain</c>) into the same <see cref="CallTreeNode"/> shape
/// <c>CpuTraceReport</c> uses for its "Hot Path" — a flat, ordered list of single-child
/// nodes rendered via <see cref="IRenderSink.CallTree"/>. That's the established,
/// purpose-built way this app displays a linear call chain (an interactive, indented tree
/// widget in HTML; a plain indented list elsewhere) — not a table cell crammed with an
/// arrow-joined string.
///
/// Each entry also carries its location and is checked against the same first-party
/// heuristic as <c>WebCpuHotspotRow.IsApplicationCode</c>, so a caller that's your own
/// code is labeled right there in the chain — no separate cross-reference against the
/// "Application Code" list needed to tell which frames are yours.
/// </summary>
internal static class WebCallChainHelper
{
    private const char Delim = ''; // matches ChromeTraceParser.BuildCallChain's entry encoding

    /// <summary>
    /// Builds the chain root-first (furthest caller at the top) down to the hotspot itself
    /// at the bottom — the natural "top of stack down to what's executing" reading, same
    /// order <c>CpuTraceAnalyzer</c> builds its hot path in.
    /// </summary>
    public static List<CallTreeNode> BuildChain(
        string leafMethod, string leafModule, string leafUrl, long leafSamples, double leafPct, string? callChain)
    {
        var chain = new List<CallTreeNode>();
        if (callChain is not null)
        {
            var entries = callChain.Split(" ← "); // nearest-first
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var parts = entries[i].Split(Delim);
                string fn       = parts.Length > 0 ? parts[0] : entries[i];
                string location = parts.Length > 1 ? parts[1] : "";
                string url      = parts.Length > 2 ? parts[2] : "";
                chain.Add(new CallTreeNode(Label(fn, url), location, 0, 0, 0, 0, []));
            }
        }
        chain.Add(new CallTreeNode(Label(leafMethod, leafUrl), leafModule, (int)leafSamples, (int)leafSamples, leafPct, leafPct, []));
        return chain;
    }

    private static string Label(string function, string url) =>
        IsApplicationUrl(url) ? $"★ {function}  (your code)" : function;

    /// <summary>
    /// Renders a built chain as a multi-line ".NET stack trace"-style block for a table
    /// cell — one frame per line, innermost (the hotspot itself) first, formatted
    /// <c>at Function in file:line</c> the same way a CLR stack trace reads. The compact,
    /// non-widget alternative to <see cref="IRenderSink.CallTree"/> for callers (like a
    /// table column) that can't host an interactive tree; the ★ markers from
    /// <see cref="BuildChain"/>'s labels carry through, so both the function name and the
    /// file it lives in are visible on every line — no separate Location column needed.
    /// </summary>
    public static string FormatStackText(IReadOnlyList<CallTreeNode> chain, int maxMethodLen = 48)
    {
        var lines = new List<string>(chain.Count);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var n  = chain[i];
            var fn = n.Method.Length > maxMethodLen ? n.Method[..maxMethodLen] + "…" : n.Method;
            lines.Add(string.IsNullOrEmpty(n.Module) ? $"at {fn}" : $"at {fn} in {n.Module}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Appends a "your code seen running nearby" block to a <see cref="FormatStackText"/>
    /// block — the one signal that survives when a hotspot has no synchronous application
    /// caller at all (e.g. reached only via a promise/deferred callback the CPU profiler
    /// can't trace through): first-party code observed running shortly before the long
    /// tasks this hotspot caused. A timing correlation, not a call-tree fact — labeled as
    /// such so it's never mistaken for one.
    /// </summary>
    public static string AppendPossibleTrigger(string stackText, string cluster)
    {
        var lines = new List<string>
        {
            stackText,
            "",
            "~ no application caller in the call tree — your code seen running nearby (timing correlation, not a call-tree link):",
        };
        lines.AddRange(cluster.Split(", ").Select(e => $"   {e}"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The nearest first-party caller in the chain, if any — used to bridge "Called From"
    /// and "Application Code": a hotspot whose *own* name is vendor code but whose chain
    /// passes through an application function is exactly the case where the two sections
    /// would otherwise look unconnected.
    /// </summary>
    public static (string Function, string Location)? FindApplicationCaller(string? callChain)
    {
        if (callChain is null) return null;
        foreach (var entry in callChain.Split(" ← "))
        {
            var parts = entry.Split(Delim);
            if (parts.Length > 2 && IsApplicationUrl(parts[2]))
                return (parts[0], parts.Length > 1 ? parts[1] : "");
        }
        return null;
    }

    /// <summary>Mirrors <c>WebCpuHotspotRow.IsApplicationCode</c> / <c>ChromeTraceParser.IsApplicationUrl</c>.</summary>
    private static bool IsApplicationUrl(string url) =>
        url.Length > 0 &&
        !url.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/.vite/deps/", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
}
