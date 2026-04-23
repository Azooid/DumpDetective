using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class StringDuplicatesReport
{
    public void Render(StringDuplicatesData data, IRenderSink sink,
        int top = 50, int minCount = 2, long minWaste = 0, string? pattern = null)
    {
        var candidates = data.Groups
            .Where(g => g.Count >= minCount)
            .Select(g =>
            {
                long perCopy = g.TotalSize / g.Count;
                long wasted  = perCopy * (g.Count - 1);
                string hint  = ClassifyString(g.Value);
                return (g.Value, g.Count, g.TotalSize, Wasted: wasted, Len: g.Value.Length, Hint: hint);
            })
            .Where(r => r.Wasted >= minWaste)
            .Where(r => pattern is null || r.Hint.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Wasted)
            .Take(top)
            .ToList();

        int  dupGroups = data.Groups.Count(g => g.Count >= 2);
        long wastedAll = data.Groups
            .Where(g => g.Count >= 2)
            .Sum(g => { long per = g.TotalSize / g.Count; return per * (g.Count - 1); });

        sink.Section("Summary");
        sink.Explain(
            what: "String deduplication analysis — finds identical strings that exist as multiple separate objects on the heap.",
            why: ".NET only interns string literals at compile time. Strings created at runtime (e.g. from databases, config, HTTP responses) are never shared automatically.",
            impact: "Each duplicate group wastes (copies − 1) × string size bytes. Thousands of identical connection strings or JSON keys can waste hundreds of megabytes.",
            bullets: ["Sort by 'Wasted' to find the highest-value targets first", "'Pattern' column classifies common shapes: path, url, guid, json, xml", "Strings > 72 chars are truncated in the display"],
            action: "Use string.Intern() for high-frequency identifiers, or a FrozenDictionary<string, string> as a shared string table."
        );
        sink.KeyValues([
            ("Total strings",     data.TotalStrings.ToString("N0")),
            ("Total string size", DumpHelpers.FormatSize(data.TotalSize)),
            ("Duplicate groups",  dupGroups.ToString("N0")),
            ("Total wasted",      DumpHelpers.FormatSize(wastedAll)),
        ]);

        if (wastedAll > 100 * 1024 * 1024)
            sink.Alert(AlertLevel.Warning, $"{DumpHelpers.FormatSize(wastedAll)} wasted on duplicate strings.",
                advice: "Use string.Intern() for high-frequency identifiers or a shared frozen dictionary.");

        if (candidates.Count == 0) { sink.Text("No duplicate strings found matching the criteria."); return; }

        var rows = candidates.Select(r =>
        {
            string display = r.Value.Length > 72 ? r.Value[..72] + "\u2026" : r.Value;
            display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return new[]
            {
                r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Wasted),
                DumpHelpers.FormatSize(r.TotalSize), r.Len.ToString("N0"),
                r.Hint.Length > 0 ? r.Hint : "—", $"\"{display}\"",
            };
        }).ToList();

        sink.Table(["Count", "Wasted", "Total", "Length", "Pattern", "Value"], rows,
            $"Top {rows.Count} duplicate groups by wasted bytes" +
            (pattern is not null ? $" (filter={pattern})" : ""));

        RenderInternCandidates(candidates, sink);
    }

    private static void RenderInternCandidates(
        IEnumerable<(string Value, int Count, long TotalSize, long Wasted, int Len, string Hint)> candidates,
        IRenderSink sink)
    {
        var internCandidates = candidates
            .Where(r => r.Count >= 100 && r.Len <= 50 && r.Hint is not "guid" and not "stackframe")
            .OrderByDescending(r => r.Wasted)
            .Take(20).ToList();
        if (internCandidates.Count == 0) return;

        sink.Section("Interning Candidates");
        sink.Alert(AlertLevel.Info,
            $"{internCandidates.Count} short string(s) duplicated 100+ times — prime candidates for string.Intern().",
            "string.Intern() returns a single canonical instance from an intern pool, eliminating duplicates.",
            "Use sparingly: interned strings live until AppDomain unload. Prefer compile-time constants for fixed identifiers.");
        var rows = internCandidates.Select(r =>
        {
            string display = r.Value.Length > 60 ? r.Value[..60] + "…" : r.Value;
            display = display.Replace("\r", "\\r").Replace("\n", "\\n");
            bool alreadyInterned = string.IsInterned(r.Value) is not null;
            return new[] { r.Count.ToString("N0"), DumpHelpers.FormatSize(r.Wasted), r.Len.ToString("N0"),
                alreadyInterned ? "Yes — BCL constant" : "", $"\"{display}\"" };
        }).ToList();
        sink.Table(["Copies", "Wasted", "Length", "CLR Interned", "Value"], rows,
            "Short strings duplicated \u2265 100 times \u2014 'CLR Interned' means already in the intern pool");
    }

    private static string ClassifyString(string s)
    {
        if (s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("ftp://",   StringComparison.OrdinalIgnoreCase)) return "url";
        if (Guid.TryParse(s, out _)) return "guid";
        if (s.StartsWith("   at ", StringComparison.Ordinal) ||
            (s.StartsWith("at ", StringComparison.Ordinal) && s.Contains('('))) return "stackframe";
        if (s.Length > 2 && (s.Contains('\\') || s.Contains('/')) && (s.Contains('.') || s.Contains(':')))
            return "path";
        if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) return "number";
        return "";
    }
}
