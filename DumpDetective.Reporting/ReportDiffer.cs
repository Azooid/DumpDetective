using DumpDetective.Core.Models;

namespace DumpDetective.Reporting;

/// <summary>
/// Diffs two <see cref="ReportDoc"/> objects (loaded from .json or .bin report files)
/// and produces a new <see cref="ReportDoc"/> showing what changed.
///
/// Tables gain a leading "Δ" column with values: Added / Removed / Changed / Same.
/// Changed cells are annotated as "old → new". Alerts and key-value pairs are matched
/// by title/key and diffed similarly.
/// </summary>
public static class ReportDiffer
{
    public sealed class Options
    {
        /// <summary>0-based column index used as the row key for table diffing (default: 0).</summary>
        public int  KeyCol      { get; init; } = 0;
        /// <summary>When true, omit chapters/sections with no changes.</summary>
        public bool ChangedOnly { get; init; } = false;
        /// <summary>When true, include unchanged rows in diff tables (default: omit them).</summary>
        public bool ShowSame    { get; init; } = false;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public static ReportDoc Diff(
        ReportDoc before,
        ReportDoc after,
        string    beforeLabel,
        string    afterLabel,
        Options?  opts = null)
    {
        opts ??= new Options();
        var result = new ReportDoc();

        var beforeIdx = IndexBy(before.Chapters, c => c.CommandName ?? c.Title);
        var afterIdx  = IndexBy(after.Chapters,  c => c.CommandName ?? c.Title);

        int added = 0, removed = 0, changed = 0, same = 0;
        var diffChapters = new List<ReportChapter>();

        // Iterate in after order, then before-only (preserves new ordering)
        var allKeys = afterIdx.Keys.Concat(beforeIdx.Keys.Where(k => !afterIdx.ContainsKey(k))).ToList();

        foreach (var key in allKeys)
        {
            beforeIdx.TryGetValue(key, out var bc);
            afterIdx .TryGetValue(key, out var ac);

            if (bc is null && ac is not null)
            {
                diffChapters.Add(PrefixChapter(ac, "[Added]"));
                added++;
            }
            else if (ac is null && bc is not null)
            {
                if (!opts.ChangedOnly)
                    diffChapters.Add(PrefixChapter(bc, "[Removed]"));
                removed++;
            }
            else if (bc is not null && ac is not null)
            {
                var (chap, hasChanges) = DiffChapters(bc, ac, opts);
                if (hasChanges)
                {
                    diffChapters.Add(chap);
                    changed++;
                }
                else
                {
                    if (!opts.ChangedOnly)
                        diffChapters.Add(chap);
                    same++;
                }
            }
        }

        // Summary chapter goes first
        result.Chapters.Add(BuildSummaryChapter(beforeLabel, afterLabel, added, removed, changed, same));
        foreach (var ch in diffChapters)
            result.Chapters.Add(ch);

        return result;
    }

    // ── Chapter-level diff ────────────────────────────────────────────────────

    private static (ReportChapter Chapter, bool HasChanges) DiffChapters(
        ReportChapter before,
        ReportChapter after,
        Options opts)
    {
        var chap = new ReportChapter
        {
            Title       = after.Title,
            Subtitle    = after.Subtitle,
            CommandName = after.CommandName,
            NavLevel    = after.NavLevel,
        };

        var beforeIdx = IndexBy(before.Sections, s => s.SectionKey ?? s.Title ?? string.Empty);
        var afterIdx  = IndexBy(after.Sections,  s => s.SectionKey ?? s.Title ?? string.Empty);
        bool hasChanges = false;

        var allKeys = afterIdx.Keys.Concat(beforeIdx.Keys.Where(k => !afterIdx.ContainsKey(k))).ToList();

        foreach (var key in allKeys)
        {
            beforeIdx.TryGetValue(key, out var bs);
            afterIdx .TryGetValue(key, out var ас);

            if (bs is null && ас is not null)
            {
                chap.Sections.Add(PrefixSection(ас, "[Added]"));
                hasChanges = true;
            }
            else if (ас is null && bs is not null)
            {
                if (!opts.ChangedOnly)
                    chap.Sections.Add(PrefixSection(bs, "[Removed]"));
                hasChanges = true;
            }
            else if (bs is not null && ас is not null)
            {
                var (sec, secChanged) = DiffSections(bs, ас, opts);
                if (secChanged || !opts.ChangedOnly)
                    chap.Sections.Add(sec);
                if (secChanged) hasChanges = true;
            }
        }

        return (chap, hasChanges);
    }

    // ── Section-level diff ────────────────────────────────────────────────────

    private static (ReportSection Section, bool HasChanges) DiffSections(
        ReportSection before,
        ReportSection after,
        Options opts)
    {
        var sec = new ReportSection
        {
            Title      = after.Title,
            SectionKey = after.SectionKey,
        };
        bool hasChanges = false;

        // KeyValues — matched by index (usually one per section)
        var beforeKVs = FilterOf<ReportKeyValues>(before.Elements);
        var afterKVs  = FilterOf<ReportKeyValues>(after.Elements);
        for (int i = 0; i < Math.Max(beforeKVs.Count, afterKVs.Count); i++)
        {
            var bkv = i < beforeKVs.Count ? beforeKVs[i] : null;
            var akv = i < afterKVs.Count  ? afterKVs[i]  : null;
            var (kv, kvChanged) = DiffKeyValues(bkv, akv, opts);
            if (kv is not null)
            {
                sec.Elements.Add(kv);
                if (kvChanged) hasChanges = true;
            }
        }

        // Alerts — matched by title
        var beforeAlerts = FilterOf<ReportAlert>(before.Elements);
        var afterAlerts  = FilterOf<ReportAlert>(after.Elements);
        var beforeAIdx   = IndexBy(beforeAlerts, a => a.Title);
        var afterAIdx    = IndexBy(afterAlerts,  a => a.Title);
        foreach (var k in afterAIdx.Keys.Concat(beforeAIdx.Keys.Where(k => !afterAIdx.ContainsKey(k))))
        {
            beforeAIdx.TryGetValue(k, out var ba);
            afterAIdx .TryGetValue(k, out var aa);
            var (ae, alertChanged) = DiffAlert(ba, aa, opts);
            if (ae is not null)
            {
                sec.Elements.Add(ae);
                if (alertChanged) hasChanges = true;
            }
        }

        // Tables — matched by position (captions may embed dump labels that differ between runs)
        var beforeTables = FilterOf<ReportTable>(before.Elements);
        var afterTables  = FilterOf<ReportTable>(after.Elements);
        // "Dump Timeline" rows are matched positionally (row i vs row i) because dump
        // filenames are always different between two trend runs, so key-based matching
        // would show everything as Added/Removed.
        bool positional = after.Title?.Contains("Dump Timeline", StringComparison.OrdinalIgnoreCase) == true
                       || before.Title?.Contains("Dump Timeline", StringComparison.OrdinalIgnoreCase) == true;
        for (int ti = 0; ti < Math.Max(beforeTables.Count, afterTables.Count); ti++)
        {
            var bt = ti < beforeTables.Count ? beforeTables[ti] : null;
            var at = ti < afterTables.Count  ? afterTables[ti]  : null;
            var (te, tableChanged) = positional
                ? DiffTableByPosition(bt, at)
                : DiffTable(bt, at, opts);
            if (te is not null)
            {
                sec.Elements.Add(te);
                if (tableChanged) hasChanges = true;
            }
        }

        // ReportDetails — include from "after" as-is (nested content not diffed)
        foreach (var rd in FilterOf<ReportDetails>(after.Elements))
            sec.Elements.Add(rd);

        return (sec, hasChanges);
    }

    // ── Table diff (positional — row i vs row i) ─────────────────────────────

    /// <summary>
    /// Diffs two tables row-by-row by position. Used for tables like Dump Timeline where
    /// the key column (dump filename) is always different between runs.
    /// </summary>
    private static (ReportTable? Table, bool HasChanges) DiffTableByPosition(
        ReportTable? before,
        ReportTable? after)
    {
        if (before is null && after is null) return (null, false);

        if (before is null)
            return (new ReportTable
            {
                Caption = after!.Caption is not null ? $"[Added] {after.Caption}" : null,
                Headers = ["\u0394", ..after.Headers],
                Rows    = after.Rows.Select(r => (string[])["Added", ..r]).ToList(),
            }, true);

        if (after is null)
            return (new ReportTable
            {
                Caption = before.Caption is not null ? $"[Removed] {before.Caption}" : null,
                Headers = ["\u0394", ..before.Headers],
                Rows    = before.Rows.Select(r => (string[])["Removed", ..r]).ToList(),
            }, true);

        int count = Math.Max(before.Rows.Count, after.Rows.Count);
        var diffRows    = new List<string[]>();
        bool hasChanges = false;

        for (int ri = 0; ri < count; ri++)
        {
            if (ri >= before.Rows.Count)
            {
                diffRows.Add(["Added", ..after.Rows[ri]]);
                hasChanges = true;
                continue;
            }
            if (ri >= after.Rows.Count)
            {
                diffRows.Add(["Removed", ..before.Rows[ri]]);
                hasChanges = true;
                continue;
            }

            var brow = before.Rows[ri];
            var arow = after.Rows[ri];
            bool rowChanged = false;
            var cells = new string[arow.Length];
            for (int ci = 0; ci < arow.Length; ci++)
            {
                string av = arow[ci];
                string bv = ci < brow.Length ? brow[ci] : string.Empty;
                if (av != bv)
                {
                    cells[ci]   = $"{bv}  \u2192  {av}";
                    rowChanged  = true;
                }
                else
                {
                    cells[ci] = av;
                }
            }

            diffRows.Add(rowChanged ? ["Changed", ..cells] : ["Same", ..arow]);
            if (rowChanged) hasChanges = true;
        }

        // Merge headers (e.g. B1 → A1)
        var mergedHeaders = new string[after.Headers.Length];
        for (int h = 0; h < after.Headers.Length; h++)
        {
            string aHdr = after.Headers[h];
            string bHdr = h < before.Headers.Length ? before.Headers[h] : string.Empty;
            mergedHeaders[h] = (bHdr.Length > 0 && bHdr != aHdr) ? $"{bHdr} \u2192 {aHdr}" : aHdr;
        }

        // Remove "Same" rows
        diffRows = diffRows.Where(r => r[0] != "Same").ToList();

        if (!hasChanges) return (null, false);

        return (new ReportTable
        {
            Caption = after.Caption is not null ? $"\u0394 {after.Caption}" : null,
            Headers = ["\u0394", ..mergedHeaders],
            Rows    = diffRows,
        }, hasChanges);
    }

    // ── Table diff ────────────────────────────────────────────────────────────

    private static (ReportTable? Table, bool HasChanges) DiffTable(
        ReportTable? before,
        ReportTable? after,
        Options opts)
    {
        if (before is null && after is null) return (null, false);

        if (before is null)
            return (new ReportTable
            {
                Caption = after!.Caption is not null ? $"[Added] {after.Caption}" : null,
                Headers = ["Δ", ..after.Headers],
                Rows    = after.Rows.Select(r => (string[])["Added", ..r]).ToList(),
            }, true);

        if (after is null)
            return (new ReportTable
            {
                Caption = before.Caption is not null ? $"[Removed] {before.Caption}" : null,
                Headers = ["Δ", ..before.Headers],
                Rows    = before.Rows.Select(r => (string[])["Removed", ..r]).ToList(),
            }, true);

        int maxSafeKey = Math.Min(
            before.Rows.Count > 0 ? before.Rows[0].Length - 1 : 0,
            after.Rows.Count  > 0 ? after.Rows[0].Length  - 1 : 0);
        int keyCol = Math.Max(0, Math.Min(opts.KeyCol, maxSafeKey));

        // If the chosen key column has duplicate values in either table, try the next columns
        // until we find one that is unique in both (e.g. skip "Severity" → use "Title").
        if (HasDuplicateKeys(before.Rows, keyCol) || HasDuplicateKeys(after.Rows, keyCol))
        {
            for (int c = keyCol + 1; c <= maxSafeKey; c++)
            {
                if (!HasDuplicateKeys(before.Rows, c) && !HasDuplicateKeys(after.Rows, c))
                {
                    keyCol = c;
                    break;
                }
            }
        }

        var beforeRowIdx = IndexRowsByKey(before.Rows, keyCol);
        var afterRowIdx  = IndexRowsByKey(after.Rows,  keyCol);

        // If key-based matching would produce zero matches (e.g. dump-label columns like A1/B1
        // that are always different between runs), fall back to positional matching so these
        // tables show "Changed" rows instead of "all Added + all Removed".
        bool noKeyOverlap = after.Rows.Count > 0 &&
                            before.Rows.Count > 0 &&
                            !after.Rows.Any(r => beforeRowIdx.ContainsKey(r.Length > keyCol ? r[keyCol] : string.Empty));
        if (noKeyOverlap)
            return DiffTableByPosition(before, after);

        var diffRows    = new List<string[]>();
        bool hasChanges = false;

        // After rows — added or changed/same
        foreach (var row in after.Rows)
        {
            var key = row.Length > keyCol ? row[keyCol] : string.Empty;
            if (!beforeRowIdx.TryGetValue(key, out var brow))
            {
                diffRows.Add(["Added", ..row]);
                hasChanges = true;
            }
            else
            {
                bool rowChanged = false;
                var  cells      = new string[row.Length];
                for (int i = 0; i < row.Length; i++)
                {
                    if (i < brow.Length && row[i] != brow[i])
                    {
                        cells[i]   = $"{brow[i]}  \u2192  {row[i]}";
                        rowChanged = true;
                    }
                    else
                    {
                        cells[i] = row[i];
                    }
                }

                if (rowChanged)
                {
                    diffRows.Add(["Changed", ..cells]);
                    hasChanges = true;
                }
                else if (opts.ShowSame)
                {
                    diffRows.Add(["Same", ..row]);
                }
            }
        }

        // Before-only rows — removed
        foreach (var row in before.Rows)
        {
            var key = row.Length > keyCol ? row[keyCol] : string.Empty;
            if (!afterRowIdx.ContainsKey(key))
            {
                diffRows.Add(["Removed", ..row]);
                hasChanges = true;
            }
        }

        if (!hasChanges && !opts.ShowSame) return (null, false);

        // Merge headers: where before and after have different column names (e.g. dump labels
        // A1/B1), show "B1 → A1" so the reader knows which side is which.
        var mergedHeaders = new string[after.Headers.Length];
        for (int h = 0; h < after.Headers.Length; h++)
        {
            string aHdr = after.Headers[h];
            string bHdr = h < before.Headers.Length ? before.Headers[h] : string.Empty;
            mergedHeaders[h] = (bHdr.Length > 0 && bHdr != aHdr)
                ? $"{bHdr} → {aHdr}"
                : aHdr;
        }

        return (new ReportTable
        {
            Caption = after.Caption is not null ? $"Δ {after.Caption}" : null,
            Headers = ["Δ", ..mergedHeaders],
            Rows    = diffRows,
        }, hasChanges);
    }

    // ── Alert diff ────────────────────────────────────────────────────────────

    private static (ReportAlert? Alert, bool HasChanges) DiffAlert(
        ReportAlert? before,
        ReportAlert? after,
        Options opts)
    {
        if (before is null && after is null) return (null, false);

        if (before is null)
            return (new ReportAlert
            {
                Level  = after!.Level,
                Title  = $"[Added] {after.Title}",
                Detail = after.Detail,
                Advice = after.Advice,
            }, true);

        if (after is null)
            return (new ReportAlert
            {
                Level  = "critical",
                Title  = $"[Removed] {before.Title}",
                Detail = before.Detail,
            }, true);

        bool levelChanged  = before.Level  != after.Level;
        bool detailChanged = before.Detail != after.Detail;
        bool adviceChanged = before.Advice != after.Advice;

        if (levelChanged || detailChanged || adviceChanged)
        {
            string note = levelChanged ? $"Level: {before.Level} → {after.Level}. " : string.Empty;
            return (new ReportAlert
            {
                Level  = after.Level,
                Title  = $"[Changed] {after.Title}",
                Detail = $"{note}{before.Detail}  →  {after.Detail}",
                Advice = after.Advice,
            }, true);
        }

        return opts.ShowSame ? (after, false) : (null, false);
    }

    // ── KeyValues diff ────────────────────────────────────────────────────────

    private static (ReportKeyValues? KV, bool HasChanges) DiffKeyValues(
        ReportKeyValues? before,
        ReportKeyValues? after,
        Options opts)
    {
        if (before is null && after is null) return (null, false);
        if (before is null) return (after, false);  // new — render as-is
        if (after is null)  return (null, false);   // removed — skip for brevity

        var beforeVals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in before.Pairs) beforeVals.TryAdd(p.Key, p.Value);

        var afterVals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in after.Pairs) afterVals.TryAdd(p.Key, p.Value);

        var diffPairs   = new List<ReportPair>();
        bool hasChanges = false;

        foreach (var pair in after.Pairs)
        {
            if (!beforeVals.TryGetValue(pair.Key, out var bval))
            {
                diffPairs.Add(new ReportPair(pair.Key, $"[New] {pair.Value}"));
                hasChanges = true;
            }
            else if (bval != pair.Value)
            {
                // If either value already contains → (e.g. "80/100 HEALTHY → 55/100 DEGRADED"),
                // use ⟹ as the diff separator so it's visually distinct from in-value arrows.
                string sep = (bval.Contains('→') || pair.Value.Contains('→')) ? "  \u27F9  " : "  →  ";
                diffPairs.Add(new ReportPair(pair.Key, $"{bval}{sep}{pair.Value}"));
                hasChanges = true;
            }
            else if (opts.ShowSame)
            {
                diffPairs.Add(pair);
            }
        }

        foreach (var pair in before.Pairs)
        {
            if (!afterVals.ContainsKey(pair.Key))
            {
                diffPairs.Add(new ReportPair(pair.Key, $"[Removed] {pair.Value}"));
                hasChanges = true;
            }
        }

        if (!hasChanges && !opts.ShowSame) return (null, false);
        return (new ReportKeyValues { Title = after.Title, Pairs = diffPairs }, hasChanges);
    }

    // ── Summary chapter ───────────────────────────────────────────────────────

    private static ReportChapter BuildSummaryChapter(
        string beforeLabel, string afterLabel,
        int added, int removed, int changed, int same)
    {
        var chap = new ReportChapter
        {
            Title    = "Diff Summary",
            Subtitle = $"{beforeLabel} → {afterLabel}",
        };

        var sec = new ReportSection { Title = "Overview" };
        sec.Elements.Add(new ReportKeyValues
        {
            Pairs =
            [
                new ReportPair("Before",             beforeLabel),
                new ReportPair("After",              afterLabel),
                new ReportPair("Chapters added",     added.ToString()),
                new ReportPair("Chapters removed",   removed.ToString()),
                new ReportPair("Chapters changed",   changed.ToString()),
                new ReportPair("Chapters unchanged", same.ToString()),
            ]
        });

        int total = added + removed + changed;
        sec.Elements.Add(new ReportAlert
        {
            Level  = total == 0 ? "info" : total <= 3 ? "warning" : "critical",
            Title  = total == 0 ? "No differences found" : $"{total} chapter(s) with changes",
            Detail = total == 0
                ? "The two reports are structurally identical."
                : $"{added} added, {removed} removed, {changed} changed.",
        });

        chap.Sections.Add(sec);
        return chap;
    }

    // ── Clone helpers ─────────────────────────────────────────────────────────

    private static ReportChapter PrefixChapter(ReportChapter chap, string prefix) => new()
    {
        Title       = $"{prefix} {chap.Title}",
        Subtitle    = chap.Subtitle,
        CommandName = chap.CommandName,
        NavLevel    = chap.NavLevel,
        Sections    = chap.Sections,
    };

    private static ReportSection PrefixSection(ReportSection sec, string prefix) => new()
    {
        Title      = $"{prefix} {sec.Title}",
        SectionKey = sec.SectionKey,
        Elements   = sec.Elements,
    };

    // ── Generic helpers ───────────────────────────────────────────────────────

    private static Dictionary<string, T> IndexBy<T>(IEnumerable<T> items, Func<T, string> keyOf)
    {
        var dict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            dict.TryAdd(keyOf(item), item);
        return dict;
    }

    private static Dictionary<string, string[]> IndexRowsByKey(List<string[]> rows, int keyCol)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var k = row.Length > keyCol ? row[keyCol] : string.Empty;
            dict.TryAdd(k, row);
        }
        return dict;
    }

    /// <summary>Returns true if any value in <paramref name="col"/> appears more than once.</summary>
    private static bool HasDuplicateKeys(List<string[]> rows, int col)
    {
        if (rows.Count <= 1) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var v = row.Length > col ? row[col] : string.Empty;
            if (!seen.Add(v)) return true;
        }
        return false;
    }

    private static List<T> FilterOf<T>(List<ReportElement> elements) where T : ReportElement
    {
        var result = new List<T>();
        foreach (var e in elements)
            if (e is T typed) result.Add(typed);
        return result;
    }
}
