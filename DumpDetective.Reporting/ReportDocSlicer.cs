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
    /// Fallback map for JSON files saved before the <c>CommandName</c> field was
    /// introduced.  Each entry maps a CLI command name to a substring that
    /// uniquely appears in that command's chapter title (case-insensitive).
    /// </summary>
    private static readonly Dictionary<string, string> LegacyTitleMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["async-stacks"]        = "Async State Machine",
            ["connection-pool"]     = "DB Connection Pool",
            ["deadlock-detection"]  = "Deadlock Detection",
            ["event-analysis"]      = "Event Analysis",
            ["exception-analysis"]  = "Exception Analysis",
            ["finalizer-queue"]     = "Finalizer Queue",
            ["gc-roots"]            = "GC Root Analysis",
            ["gen-summary"]         = "GC Generation Summary",
            ["handle-table"]        = "GC Handle Table",
            ["heap-fragmentation"]  = "Heap Fragmentation",
            ["heap-stats"]          = "Heap Statistics",
            ["high-refs"]           = "Highly Referenced",
            ["http-requests"]       = "HTTP Objects",
            ["large-objects"]       = "Large Objects",
            ["memory-leak"]         = "Memory Leak",
            ["module-list"]         = "Module List",
            ["pinned-objects"]      = "Pinned Objects",
            ["static-refs"]         = "Static Reference",
            ["string-duplicates"]   = "String Duplicates",
            ["thread-analysis"]     = "Thread Analysis",
            ["thread-pool"]         = "Thread Pool Analysis",
            ["timer-leaks"]         = "Timer Leak",
            ["type-instances"]      = "Type Instances",
            ["wcf-channels"]        = "WCF Channels",
            ["weak-refs"]           = "Weak References",
        };

    /// <summary>
    /// Returns a new <see cref="ReportDoc"/> containing only the chapters whose
    /// <see cref="ReportChapter.CommandName"/> matches one of
    /// <paramref name="commandNames"/> (case-insensitive).
    /// For chapters without a <c>CommandName</c> (JSON saved before the field was
    /// introduced), falls back to title-substring matching via <see cref="LegacyTitleMap"/>.
    /// The untagged analyze-summary chapter is included when <c>"analyze"</c> is
    /// in <paramref name="commandNames"/>.
    /// </summary>
    public static ReportDoc Slice(ReportDoc source, IReadOnlyList<string> commandNames)
    {
        var set = new HashSet<string>(commandNames, StringComparer.OrdinalIgnoreCase);
        bool includeUntagged = set.Contains("analyze");

        // Pre-build the title substrings we need to match for legacy chapters
        var legacySubstrings = set
            .Where(LegacyTitleMap.ContainsKey)
            .Select(n => LegacyTitleMap[n])
            .ToList();

        var result = new ReportDoc();
        foreach (var ch in source.Chapters)
        {
            bool match;
            if (ch.CommandName is not null)
            {
                // New JSON: exact command-name match
                match = set.Contains(ch.CommandName);
            }
            else
            {
                // Legacy JSON: fall back to title-substring match
                match = includeUntagged ||
                        legacySubstrings.Any(sub =>
                            ch.Title.Contains(sub, StringComparison.OrdinalIgnoreCase));
            }

            if (match)
                result.Chapters.Add(ch);
        }
        return result;
    }

    /// <summary>
    /// Returns the distinct command names present in <paramref name="doc"/>,
    /// sorted. Uses <see cref="ReportChapter.CommandName"/> when set; falls back
    /// to reverse-looking up the chapter title in <see cref="LegacyTitleMap"/>
    /// for older JSON files. Chapters that match neither are reported as <c>"analyze"</c>.
    /// </summary>
    public static IReadOnlyList<string> AvailableCommands(ReportDoc doc)
    {
        // Reverse map: title substring → command name (for legacy lookup)
        var reverseMap = LegacyTitleMap
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        return doc.Chapters
                  .Select(ch =>
                  {
                      if (ch.CommandName is not null)
                          return ch.CommandName;

                      // Legacy: find the first title substring that matches
                      foreach (var (sub, cmd) in LegacyTitleMap)
                          if (ch.Title.Contains(sub, StringComparison.OrdinalIgnoreCase))
                              return cmd;

                      return "analyze";
                  })
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Order(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }
}
