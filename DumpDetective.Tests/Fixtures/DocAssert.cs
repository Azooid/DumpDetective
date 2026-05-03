using DumpDetective.Core.Models;

namespace DumpDetective.Tests.Fixtures;

/// <summary>
/// Helpers for asserting structural properties of a <see cref="ReportDoc"/>
/// without hard-coding section titles or column positions.
/// </summary>
public static class DocAssert
{
    // ── Chapter / Section presence ────────────────────────────────────────────

    /// <summary>Asserts the doc has at least one chapter with at least one section.</summary>
    public static void HasContent(ReportDoc doc)
    {
        Assert.NotNull(doc);
        Assert.NotEmpty(doc.Chapters);
        Assert.True(
            doc.Chapters.Any(c => c.Sections.Count > 0),
            $"Expected at least one non-empty chapter. Chapters: {string.Join(", ", doc.Chapters.Select(c => c.Title))}");
    }

    /// <summary>Returns all sections from the document (flattened).</summary>
    public static IEnumerable<ReportSection> AllSections(ReportDoc doc) =>
        doc.Chapters.SelectMany(c => c.Sections);

    /// <summary>
    /// Asserts that at least one section title contains <paramref name="partialTitle"/>
    /// (case-insensitive substring match).
    /// </summary>
    public static void HasSection(ReportDoc doc, string partialTitle)
    {
        var all = AllSections(doc).Select(s => s.Title ?? "").ToList();
        Assert.True(
            all.Any(t => t.Contains(partialTitle, StringComparison.OrdinalIgnoreCase)),
            $"Expected a section containing '{partialTitle}'. " +
            $"Sections found: {string.Join(", ", all.Select(t => $"'{t}'"))}");
    }

    // ── Table helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all <see cref="ReportTable"/> elements anywhere in the document.
    /// </summary>
    public static IReadOnlyList<ReportTable> AllTables(ReportDoc doc)
    {
        var tables = new List<ReportTable>();
        foreach (var chapter in doc.Chapters)
            foreach (var section in chapter.Sections)
                CollectTables(section.Elements, tables);
        return tables;
    }

    private static void CollectTables(IEnumerable<ReportElement> elements, List<ReportTable> tables)
    {
        foreach (var el in elements)
        {
            if (el is ReportTable t) tables.Add(t);
            if (el is ReportDetails d) CollectTables(d.Elements, tables);
        }
    }

    /// <summary>
    /// Returns the first table whose Headers array contains ALL of
    /// <paramref name="requiredHeaders"/> (case-insensitive), or null.
    /// </summary>
    public static ReportTable? TryTableByHeaders(ReportDoc doc, params string[] requiredHeaders)
    {
        foreach (var t in AllTables(doc))
            if (requiredHeaders.All(h => t.Headers.Any(th => th.Contains(h, StringComparison.OrdinalIgnoreCase))))
                return t;
        return null;
    }

    /// <summary>
    /// Asserts a table whose Headers contain all <paramref name="requiredHeaders"/> exists
    /// AND has ≥ <paramref name="minRows"/> data rows.
    /// </summary>
    public static void TableByHeadersHasMinRows(ReportDoc doc, int minRows, string reason,
        params string[] requiredHeaders)
    {
        var t = TryTableByHeaders(doc, requiredHeaders);
        if (t is null)
        {
            var allHeaders = AllTables(doc).Select(tbl => string.Join("|", tbl.Headers)).ToList();
            Assert.Fail(
                $"No table with headers [{string.Join(", ", requiredHeaders)}] found ({reason}). " +
                $"All table headers: {string.Join(" | ", allHeaders)}");
        }
        Assert.True(t!.Rows.Count >= minRows,
            $"Table [{string.Join(", ", t.Headers)}] has {t.Rows.Count} rows, " +
            $"expected ≥ {minRows} ({reason}).");
    }

    /// <summary>
    /// Finds the first table where the predicate over its rows returns true,
    /// or fails the test with a descriptive message.
    /// </summary>
    public static ReportTable FindTable(ReportDoc doc, Func<IReadOnlyList<string[]>, bool> rowPredicate, string reason)
    {
        foreach (var t in AllTables(doc))
            if (rowPredicate(t.Rows)) return t;

        var summary = string.Join(", ", AllTables(doc)
            .Select(t => $"[rows={t.Rows.Count}, headers={string.Join("|", t.Headers)}]"));

        Assert.Fail($"No table satisfied: {reason}. Tables found: {summary}");
        return null!; // unreachable
    }

    /// <summary>
    /// Asserts that at least one table in the document has ≥ <paramref name="minRows"/> rows.
    /// </summary>
    public static void TableHasMinRows(ReportDoc doc, int minRows, string reason = "")
    {
        var tables = AllTables(doc);
        Assert.True(
            tables.Any(t => t.Rows.Count >= minRows),
            $"Expected a table with >= {minRows} rows{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}. " +
            $"Tables: {string.Join(", ", tables.Select(t => t.Rows.Count))}");
    }

    /// <summary>
    /// Parses the first cell of every row in all tables as a long and asserts
    /// that at least one parsed value is ≥ <paramref name="minCount"/>.
    /// </summary>
    public static void AnyTableRowCountAtLeast(ReportDoc doc, long minCount, string reason = "")
    {
        foreach (var table in AllTables(doc))
            foreach (var row in table.Rows)
                if (row.Length > 0 && long.TryParse(row[0].Replace(",", ""), out long v) && v >= minCount)
                    return;

        Assert.Fail(
            $"Expected a table row with count >= {minCount}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}.");
    }

    /// <summary>
    /// Asserts that at least one table contains a row where any cell contains
    /// <paramref name="text"/> (case-insensitive substring match).
    /// </summary>
    public static void AnyTableContainsText(ReportDoc doc, string text, string reason = "")
    {
        foreach (var table in AllTables(doc))
            foreach (var row in table.Rows)
                if (row.Any(cell => cell.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    return;

        Assert.Fail(
            $"Expected a table cell containing '{text}'" +
            $"{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}.");
    }

    // ── KeyValues helpers ─────────────────────────────────────────────────────

    private static IEnumerable<ReportKeyValues> AllKeyValueElements(ReportDoc doc)
    {
        foreach (var chapter in doc.Chapters)
            foreach (var section in chapter.Sections)
                foreach (var el in CollectElements(section.Elements))
                    if (el is ReportKeyValues kv) yield return kv;
    }

    private static IEnumerable<ReportElement> CollectElements(IEnumerable<ReportElement> elements)
    {
        foreach (var el in elements)
        {
            yield return el;
            if (el is ReportDetails d)
                foreach (var inner in CollectElements(d.Elements))
                    yield return inner;
        }
    }

    /// <summary>
    /// Asserts that at least one <see cref="ReportKeyValues"/> element contains a pair
    /// whose Key equals <paramref name="key"/> (case-insensitive).
    /// </summary>
    public static void HasKeyValue(ReportDoc doc, string key)
    {
        var found = AllKeyValueElements(doc)
            .Any(kv => kv.Pairs.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
        if (!found)
        {
            var allKeys = AllKeyValueElements(doc)
                .SelectMany(kv => kv.Pairs.Select(p => p.Key)).ToList();
            Assert.Fail(
                $"Expected a KeyValues pair with key '{key}'. " +
                $"Keys found: {string.Join(", ", allKeys.Select(k => $"'{k}'"))}");
        }
    }

    /// <summary>
    /// Returns the value of the first KeyValues pair whose Key equals
    /// <paramref name="key"/> (case-insensitive), or null if not found.
    /// </summary>
    public static string? GetKeyValue(ReportDoc doc, string key) =>
        AllKeyValueElements(doc)
            .SelectMany(kv => kv.Pairs)
            .FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    // ── Details helpers ───────────────────────────────────────────────────────

    private static IEnumerable<string> AllDetailsTitles(IEnumerable<ReportElement> elements)
    {
        foreach (var el in elements)
        {
            if (el is ReportDetails d)
            {
                yield return d.Title;
                foreach (var t in AllDetailsTitles(d.Elements)) yield return t;
            }
        }
    }

    /// <summary>
    /// Asserts that at least one <see cref="ReportDetails"/> title anywhere in the document
    /// contains <paramref name="text"/> (case-insensitive substring match).
    /// </summary>
    public static void AnyDetailsTitleContains(ReportDoc doc, string text, string reason = "")
    {
        foreach (var chapter in doc.Chapters)
            foreach (var section in chapter.Sections)
                foreach (var title in AllDetailsTitles(section.Elements))
                    if (title.Contains(text, StringComparison.OrdinalIgnoreCase))
                        return;

        Assert.Fail(
            $"Expected a details block title containing '{text}'" +
            $"{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}.");
    }

    // ── Alert helpers ─────────────────────────────────────────────────────────

    /// <summary>Returns all <see cref="ReportAlert"/> elements anywhere in the document.</summary>
    public static IEnumerable<ReportAlert> AllAlerts(ReportDoc doc)
    {
        foreach (var chapter in doc.Chapters)
            foreach (var section in chapter.Sections)
                foreach (var el in CollectElements(section.Elements))
                    if (el is ReportAlert a) yield return a;
    }

    /// <summary>
    /// Asserts that at least one <see cref="ReportAlert"/> Title, Detail, or Advice
    /// contains <paramref name="partialText"/> (case-insensitive).
    /// </summary>
    public static void AlertContains(ReportDoc doc, string partialText)
    {
        var found = AllAlerts(doc).Any(a =>
            a.Title.Contains(partialText, StringComparison.OrdinalIgnoreCase) ||
            (a.Detail?.Contains(partialText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (a.Advice?.Contains(partialText, StringComparison.OrdinalIgnoreCase) ?? false));

        if (!found)
        {
            var allTitles = AllAlerts(doc).Select(a => $"'{a.Title}'").ToList();
            Assert.Fail(
                $"Expected an alert containing '{partialText}'. " +
                $"Alert titles found: {string.Join(", ", allTitles)}");
        }
    }
}
