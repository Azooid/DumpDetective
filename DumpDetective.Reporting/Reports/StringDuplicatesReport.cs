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

        // Waste ratio gauge
        if (data.TotalSize > 0)
        {
            double wastePct = wastedAll * 100.0 / data.TotalSize;
            sink.Gauges([("Wasted / total string bytes", wastePct, "%")], barMax: 100.0);
        }

        // Top 8 duplicate groups by wasted bytes — donut
        var dupSegs = candidates.Take(8)
            .Select(r => {
                string lbl = r.Value.Length > 30 ? r.Value[..30] + "\u2026" : r.Value;
                return (Label: lbl, Value: (double)r.Wasted);
            })
            .ToList();
        if (dupSegs.Count > 0)
            sink.DonutChart(dupSegs, "Top duplicate groups by wasted bytes",
                $"{DumpHelpers.FormatSize(wastedAll)}\nwasted");

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

        // Pattern/hint distribution donut
        var hintSegs = candidates
            .GroupBy(r => r.Hint.Length > 0 ? r.Hint : "other")
            .OrderByDescending(g => g.Sum(r => r.Wasted))
            .Select(g => (g.Key, (double)g.Sum(r => r.Wasted)))
            .ToList();
        if (hintSegs.Count > 1)
            sink.DonutChart(hintSegs, "Wasted bytes by string pattern",
                $"{DumpHelpers.FormatSize(wastedAll)}\nwasted");

        RenderInternCandidates(candidates, sink);
        RenderByteArrayGroups(data, sink);
        RenderEncodingGroups(data, sink);
    }

    private static void RenderByteArrayGroups(StringDuplicatesData data, IRenderSink sink)
    {
        if (data.ByteArrayGroups is not { Count: > 0 }) return;

        sink.Section("Duplicate byte[] Arrays");
        sink.Explain(
            what: "Byte arrays with identical content detected as multiple separate allocations on the heap.",
            why:  "Duplicate byte arrays arise from repeated serialization, HTTP response caching, or token/key storage. " +
                  "Unlike strings, byte arrays are never interned automatically. " +
                  "Pooling identical buffers can eliminate significant allocation churn.",
            bullets:
            [
                "Short arrays (< 64 B) with many copies \u2192 token, key, or constant data — use static readonly or ArrayPool",
                "Medium arrays (64 B–4 KB) repeated many times \u2192 HTTP body fragments or encoding buffers",
                "Deduplicate by caching a canonical instance per content hash",
            ]);

        long totalWasted = data.ByteArrayGroups.Sum(g => (long)g.ArrayLength * (g.Count - 1));
        sink.KeyValues([
            ("Duplicate byte[] groups", data.ByteArrayGroups.Count.ToString("N0")),
            ("Total wasted",            DumpHelpers.FormatSize(totalWasted)),
        ]);

        var rows = data.ByteArrayGroups
            .OrderByDescending(g => (long)g.ArrayLength * (g.Count - 1))
            .Take(50)
            .Select(g =>
            {
                long wasted = (long)g.ArrayLength * (g.Count - 1);
                return new[]
                {
                    g.Count.ToString("N0"),
                    g.ArrayLength.ToString("N0"),
                    DumpHelpers.FormatSize(g.TotalSize),
                    DumpHelpers.FormatSize(wasted),
                    g.Preview.Length > 60 ? g.Preview[..60] + "\u2026" : g.Preview,
                };
            })
            .ToList();

        sink.Table(["Count", "Length (bytes)", "Total Size", "Wasted", "Content Preview (hex)"], rows,
            "Top 50 duplicate byte[] groups by wasted bytes. Preview = first 16 bytes as hex.");
    }

    private static void RenderEncodingGroups(StringDuplicatesData data, IRenderSink sink)
    {
        if (data.EncodingGroups is not { Count: > 0 }) return;

        sink.Section("String Encoding Waste");
        sink.Explain(
            what: "Analysis of top duplicate strings by their encoding characteristics. " +
                  "UTF-8 waste = bytes that could be saved by storing the string as UTF-8 instead of UTF-16.",
            why:  ".NET strings are stored as UTF-16 (2 bytes per character). For pure ASCII content, " +
                  "this doubles memory compared to a UTF-8 encoding. High UTF-8 waste signals that strings " +
                  "could be stored more efficiently (e.g. as ReadOnlyMemory<byte> or in a native buffer).",
            bullets:
            [
                "IsAllAscii = all characters fit in 7-bit ASCII (UTF-8 would save 50%)",
                "IsAllLatin1 = all characters are in ISO-8859-1 range (UTF-8 savings ~50% for chars <= 127)",
                "Utf8WasteBytes = (string length in UTF-16) - (estimated UTF-8 length) × Count",
            ]);

        long totalWaste = data.EncodingGroups.Sum(g => g.Utf8WasteBytes);
        sink.KeyValues([
            ("Groups analyzed",   data.EncodingGroups.Count.ToString("N0")),
            ("Total UTF-8 waste", DumpHelpers.FormatSize(totalWaste)),
        ]);

        var rows = data.EncodingGroups
            .OrderByDescending(g => g.Utf8WasteBytes)
            .Take(50)
            .Select(g =>
            {
                string display = g.Value.Length > 55 ? g.Value[..55] + "\u2026" : g.Value;
                display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                return new[]
                {
                    g.Count.ToString("N0"),
                    DumpHelpers.FormatSize(g.TotalSize),
                    DumpHelpers.FormatSize(g.Utf8WasteBytes),
                    g.IsAllAscii  ? "ASCII"  :
                    g.IsAllLatin1 ? "Latin1" : "Unicode",
                    $"\"{display}\"",
                };
            })
            .ToList();

        sink.Table(["Count", "Total Size (UTF-16)", "UTF-8 Waste", "Encoding", "Value"], rows,
            "UTF-8 Waste = bytes saved if the string were stored as UTF-8 instead of UTF-16, across all copies.");
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
