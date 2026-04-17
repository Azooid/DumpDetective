using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class TimerLeaksReport
{
    public void Render(TimerLeaksData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section("Summary");
        if (data.Timers.Count == 0) { sink.Text("No timer objects found."); return; }

        long totalSize = data.Timers.Sum(t => t.Size);
        sink.KeyValues([
            ("Total timer objects", data.Timers.Count.ToString("N0")),
            ("Total size",          Fmt(totalSize)),
        ]);

        if (data.Timers.Count > 500)
            sink.Alert(AlertLevel.Critical, $"{data.Timers.Count:N0} timer objects on heap.",
                advice: "Dispose System.Timers.Timer when no longer needed. Prefer System.Threading.PeriodicTimer (auto-disposing).");
        else if (data.Timers.Count > 100)
            sink.Alert(AlertLevel.Warning, $"{data.Timers.Count:N0} timer objects detected.");

        RenderTypeGroups(data, sink, showAddr);
        RenderPeriodDistribution(data, sink);
    }

    private static void RenderTypeGroups(TimerLeaksData data, IRenderSink sink, bool showAddr)
    {
        foreach (var g in data.Timers.GroupBy(t => t.Type).OrderByDescending(g => g.Count()))
        {
            long grpSize = g.Sum(t => t.Size);
            sink.BeginDetails($"{g.Key}  —  {g.Count():N0} instance(s)  |  {Fmt(grpSize)}", open: g.Count() > 10);

            var cbRows = g
                .GroupBy(t => t.Callback.Length > 0 ? t.Callback : "<unknown>")
                .OrderByDescending(cg => cg.Count())
                .Select(cg => new[]
                {
                    cg.Key,
                    cg.First().Module.Length > 0 ? cg.First().Module : "—",
                    cg.Count().ToString("N0"),
                    FormatInterval(cg.First().PeriodMs),
                    FormatInterval(cg.First().DueMs),
                })
                .ToList();
            sink.Table(["Callback Method", "Module (DLL)", "Count", "Period", "Due In"], cbRows);

            if (showAddr)
            {
                var addrRows = g.Take(200).Select(t => new[]
                {
                    $"0x{t.Addr:X16}",
                    t.Callback.Length > 0 ? t.Callback : "—",
                    FormatInterval(t.PeriodMs),
                    FormatInterval(t.DueMs),
                    Fmt(t.Size),
                }).ToList();
                sink.Table(["Address", "Callback", "Period", "Due In", "Size"], addrRows);
            }
            sink.EndDetails();
        }
    }

    private static void RenderPeriodDistribution(TimerLeaksData data, IRenderSink sink)
    {
        sink.Section("Timer Period Distribution");
        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in data.Timers)
        {
            string bucket = t.PeriodMs switch
            {
                -1          => "Infinite (one-shot)",
                0           => "0 ms (immediate)",
                < 100       => "< 100 ms (high-frequency)",
                < 1_000     => "100 ms – 1 s",
                < 10_000    => "1 s – 10 s",
                < 60_000    => "10 s – 1 min",
                < 3_600_000 => "1 min – 1 hr",
                _           => "> 1 hr",
            };
            buckets[bucket] = buckets.GetValueOrDefault(bucket, 0) + 1;
        }
        var rows = buckets
            .OrderBy(kv => PeriodSortKey(kv.Key))
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") })
            .ToList();
        sink.Table(["Period Range", "Count"], rows, "High-frequency timers cause CPU overhead");

        int highFreq = data.Timers.Count(t => t.PeriodMs >= 0 && t.PeriodMs < 100);
        if (highFreq > 10)
            sink.Alert(AlertLevel.Warning,
                $"{highFreq} timer(s) firing more than 10×/sec (period < 100 ms).",
                "High-frequency timers create CPU and GC pressure.",
                "Consolidate short-interval timers into a single dispatcher or use System.Threading.PeriodicTimer.");
    }

    private static string FormatInterval(long ms) => ms switch
    {
        -1        => "Infinite",
        0         => "0 ms",
        < 1000    => $"{ms} ms",
        < 60000   => $"{ms / 1000.0:F1}s",
        < 3600000 => $"{ms / 60000.0:F1}m",
        _         => $"{ms / 3600000.0:F1}h",
    };

    private static int PeriodSortKey(string bucket) => bucket switch
    {
        "0 ms (immediate)"           => 0,
        "< 100 ms (high-frequency)"  => 1,
        "100 ms – 1 s"               => 2,
        "1 s – 10 s"                 => 3,
        "10 s – 1 min"               => 4,
        "1 min – 1 hr"               => 5,
        "> 1 hr"                     => 6,
        _                            => 7,
    };

    private static string Fmt(long b) => DumpHelpers.FormatSize(b);
}
