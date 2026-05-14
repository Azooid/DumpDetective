using DumpDetective.Analysis.Trace;
using DumpDetective.Core.Interfaces;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Renders the "Unique Event Types" appendix section shared by
/// <see cref="TraceAnalyzeCommand"/> and <see cref="TraceDumpAnalyzeCommand"/>.
///
/// Layout:
///   Overview — key-value totals
///   One collapsible accordion per provider (sorted alpha), events inside sorted by count desc.
///   Uncovered providers (no consumer) are visually distinguished in the title.
/// </summary>
internal static class TraceEventTypesSection
{
    private const int MaxEventsPerProvider = 500;

    internal static void Render(IRenderSink sink, DispatchStats stats)
    {
        if (stats.TotalEvents == 0 || stats.EventCounts.Count == 0)
            return;

        sink.Header("Event Type Inventory", "", navLevel: 2);

        // ── Partition + aggregate ─────────────────────────────────────────────
        int  uniqueTypes     = stats.EventCounts.Count;
        int  coveredTypes    = 0;
        int  uncoveredTypes  = 0;
        long coveredEvents   = 0;
        long uncoveredEvents = 0;

        foreach (var kvp in stats.EventCounts)
        {
            stats.ConsumerCounts.TryGetValue(kvp.Key, out int cc);
            if (cc > 0) { coveredTypes++;   coveredEvents   += kvp.Value; }
            else        { uncoveredTypes++; uncoveredEvents += kvp.Value; }
        }

        // ── Overview ──────────────────────────────────────────────────────────
        sink.Section("Overview", "event-types-overview");
        sink.KeyValues([
            ("Total events",           stats.TotalEvents.ToString("N0")),
            ("Unique event types",     uniqueTypes.ToString("N0")),
            ("Covered types",          $"{coveredTypes:N0}  ({coveredTypes * 100 / Math.Max(1, uniqueTypes):F0}% of unique types)"),
            ("Covered event volume",   $"{coveredEvents:N0}  ({coveredEvents * 100.0 / Math.Max(1, stats.TotalEvents):F1}% of all events)"),
            ("Uncovered types",        $"{uncoveredTypes:N0}  ({uncoveredTypes * 100 / Math.Max(1, uniqueTypes):F0}% of unique types)"),
            ("Uncovered event volume", $"{uncoveredEvents:N0}  ({uncoveredEvents * 100.0 / Math.Max(1, stats.TotalEvents):F1}% of all events)"),
        ]);

        // ── Build per-provider groups ─────────────────────────────────────────
        // provider → list of (bareEventName, count, consumerCount)
        var groups = new Dictionary<string, List<(string EventName, long Count, int Consumers)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in stats.EventCounts)
        {
            stats.ProviderNames.TryGetValue(kvp.Key, out string? rawProvider);
            stats.ConsumerCounts.TryGetValue(kvp.Key, out int cc);
            string provider = rawProvider is { Length: > 0 } p ? p : ExtractProvider(kvp.Key);
            string evName   = BareEventName(kvp.Key);

            if (!groups.TryGetValue(provider, out var list))
                groups[provider] = list = new List<(string, long, int)>();
            list.Add((evName, kvp.Value, cc));
        }

        // Sort providers alpha; within each provider sort by count desc then event name asc
        var providerList = new List<string>(groups.Keys);
        providerList.Sort(StringComparer.OrdinalIgnoreCase);

        // ── One accordion per provider ────────────────────────────────────────
        sink.Section("By Provider", "event-types-by-provider");

        foreach (var provider in providerList)
        {
            var events = groups[provider];
            events.Sort(static (a, b) =>
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : string.Compare(a.EventName, b.EventName, StringComparison.OrdinalIgnoreCase);
            });

            long   providerTotal    = 0;
            int    coveredInGroup   = 0;
            foreach (var e in events)
            {
                providerTotal += e.Count;
                if (e.Consumers > 0) coveredInGroup++;
            }

            double providerPct = stats.TotalEvents > 0 ? providerTotal * 100.0 / stats.TotalEvents : 0;
            bool   allCovered  = coveredInGroup == events.Count;
            bool   noneCovered = coveredInGroup == 0;

            string coverage = allCovered  ? "all covered" :
                              noneCovered ? "no consumers" :
                              $"{coveredInGroup}/{events.Count} covered";

            string title = $"{(provider.Length > 0 ? provider : "(no provider)")}  —  " +
                           $"{events.Count} event type{(events.Count == 1 ? "" : "s")}  •  " +
                           $"{providerTotal:N0} events  ({providerPct:F2}%)  •  {coverage}";

            // First provider expanded by default so something is visible immediately.
            bool open = provider == providerList[0];
            sink.BeginDetails(title, open);

            int display = Math.Min(events.Count, MaxEventsPerProvider);
            var rows    = new string[display][];
            for (int i = 0; i < display; i++)
            {
                var (evName, count, consumers) = events[i];
                double pct = stats.TotalEvents > 0 ? count * 100.0 / stats.TotalEvents : 0;
                rows[i] =
                [
                    evName,
                    count.ToString("N0"),
                    $"{pct:F2}%",
                    consumers > 0 ? consumers.ToString() : "—",
                ];
            }
            sink.Table(["Event Name", "Count", "% of Total", "Consumers"], rows);

            if (display < events.Count)
                sink.Text($"… {events.Count - display} more event types not shown.");

            sink.EndDetails();
        }
    }

    private static string BareEventName(string raw)
    {
        int slash = raw.LastIndexOf('/');
        return slash < 0 ? raw : raw[(slash + 1)..];
    }

    private static string ExtractProvider(string raw)
    {
        int slash = raw.IndexOf('/');
        return slash < 0 ? "" : raw[..slash];
    }
}
