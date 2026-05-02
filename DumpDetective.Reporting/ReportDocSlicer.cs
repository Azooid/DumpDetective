using DumpDetective.Core.Models;

namespace DumpDetective.Reporting;

/// <summary>
/// Extracts a subset of chapters from a <see cref="ReportDoc"/> by command name.
/// Used by <c>render --command</c> to produce a single-command report from a
/// trend-raw sub-report without re-analyzing the dump.
/// </summary>
public static class ReportDocSlicer
{
    /// <summary>
    /// Returns a new <see cref="ReportDoc"/> containing only the chapters whose
    /// <see cref="ReportChapter.CommandName"/> matches one of
    /// <paramref name="commandNames"/> (case-insensitive).
    /// The analyze-summary chapter (CommandName = null) is included when <c>"analyze"</c>
    /// is in <paramref name="commandNames"/>.
    /// </summary>
    public static ReportDoc Slice(ReportDoc source, IReadOnlyList<string> commandNames)
    {
        var set = new HashSet<string>(commandNames, StringComparer.OrdinalIgnoreCase);
        bool includeUntagged = set.Contains("analyze");

        var result = new ReportDoc();
        foreach (var ch in source.Chapters)
        {
            bool match = ch.CommandName is not null
                ? set.Contains(ch.CommandName)
                : includeUntagged;

            if (match)
                result.Chapters.Add(ch);
        }
        return result;
    }

    /// <summary>
    /// Returns the distinct command names present in <paramref name="doc"/>, sorted.
    /// Chapters without a <c>CommandName</c> are reported as <c>"analyze"</c>.
    /// </summary>
    public static IReadOnlyList<string> AvailableCommands(ReportDoc doc) =>
        doc.Chapters
           .Select(ch => ch.CommandName ?? "analyze")
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .Order(StringComparer.OrdinalIgnoreCase)
           .ToList();
}
