using System.Runtime.InteropServices;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Analysis.Consumers;

namespace DumpDetective.Analysis;

/// <summary>
/// Static helpers that convert raw consumer results into the sorted list fields
/// of <see cref="DumpSnapshot"/>. Shared by both the full and lightweight heap
/// collection paths so the conversion logic is defined exactly once.
/// </summary>
internal static class SnapshotPopulator
{
    internal static void ApplyTopTypes(
        DumpSnapshot s,
        IEnumerable<KeyValuePair<string, DumpDetective.Core.Runtime.TypeAgg>> typeStats,
        int take = 30)
    {
        var list = new List<TypeStat>();
        foreach (var kv in typeStats)
            list.Add(new TypeStat(kv.Value.Name, kv.Value.Count, kv.Value.Size));
        list.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
        s.TopTypes = list;
    }

    internal static void ApplyExceptionCounts(DumpSnapshot s, IReadOnlyDictionary<string, int> totals)
    {
        var list = new List<NameCount>(totals.Count);
        foreach (var kv in totals) list.Add(new NameCount(kv.Key, kv.Value));
        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        s.ExceptionCounts = list;
    }

    internal static void ApplyAsyncMethods(DumpSnapshot s, Dictionary<string, int> methodCounts, int backlogTotal, int take = 10)
    {
        s.AsyncBacklogTotal = backlogTotal;
        var list = new List<NameCount>(methodCounts.Count);
        foreach (var kv in methodCounts) list.Add(new NameCount(kv.Key, kv.Value));
        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
        s.TopAsyncMethods = list;
    }

    internal static void ApplyStringDuplicates(
        DumpSnapshot s,
        Dictionary<string, (int Count, long TotalSize)> stringGroups,
        int take = 10)
    {
        s.UniqueStringCount = stringGroups.Count;
        int duplicateGroups = 0;
        long wastedBytes    = 0;
        var stats = new List<StringDuplicateStat>();

        foreach (var kv in stringGroups)
        {
            if (kv.Value.Count < 2) continue;
            duplicateGroups++;
            long perCopy = kv.Value.TotalSize / kv.Value.Count;
            long wasted  = perCopy * (kv.Value.Count - 1);
            wastedBytes += wasted;
            stats.Add(new StringDuplicateStat(kv.Key, kv.Value.Count, wasted));
        }

        s.StringDuplicateGroups = duplicateGroups;
        s.StringWastedBytes     = wastedBytes;
        stats.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        if (stats.Count > take) stats.RemoveRange(take, stats.Count - take);
        s.TopStringDuplicates = stats;
    }

    internal static void ApplyEventLeaks(
        DumpSnapshot s,
        Dictionary<(string Publisher, string Field), int> eventLeakTotals,
        int take = 10)
    {
        if (eventLeakTotals.Count == 0) return;

        var grouped = new List<EventLeakStat>(eventLeakTotals.Count);
        foreach (var kv in eventLeakTotals)
            grouped.Add(new EventLeakStat(kv.Key.Publisher, kv.Key.Field, kv.Value));
        grouped.Sort(static (a, b) => b.Subscribers.CompareTo(a.Subscribers));

        int total = 0, maxOnField = 0;
        foreach (var item in grouped)
        {
            total += item.Subscribers;
            if (item.Subscribers > maxOnField) maxOnField = item.Subscribers;
        }

        s.EventLeakFieldCount  = grouped.Count;
        s.EventSubscriberTotal = total;
        s.EventLeakMaxOnField  = maxOnField;
        if (grouped.Count > take) grouped.RemoveRange(take, grouped.Count - take);
        s.TopEventLeaks = grouped;
    }

    /// <summary>
    /// Variant for use when the caller already has a sorted
    /// <see cref="IReadOnlyList{EventLeakGroup}"/> (e.g. from
    /// <see cref="Analyzers.EventAnalysisAnalyzer"/>).
    /// </summary>
    internal static void ApplyEventLeaks(
        DumpSnapshot s,
        IReadOnlyList<EventLeakGroup> groups,
        int take = 10)
    {
        if (groups.Count == 0) return;

        int total = 0, maxOnField = 0;
        foreach (var g in groups)
        {
            total += g.Subscribers;
            if (g.Subscribers > maxOnField) maxOnField = g.Subscribers;
        }

        s.EventLeakFieldCount  = groups.Count;
        s.EventSubscriberTotal = total;
        s.EventLeakMaxOnField  = maxOnField;

        int cap = Math.Min(groups.Count, take);
        var top = new List<EventLeakStat>(cap);
        for (int i = 0; i < cap; i++)
            top.Add(new EventLeakStat(groups[i].Publisher, groups[i].Field, groups[i].Subscribers));
        s.TopEventLeaks = top;
    }
}
