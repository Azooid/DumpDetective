using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Rolls up every web sub-analyzer's findings into one health score + ranked Action
/// Queue + "Look here first" pointer, then replays each sub-analyzer's already-rendered
/// section in a fixed order — same shape as <c>AnalyzeReport</c>'s full-analyze rollup,
/// scaled down to the handful of web analyzers instead of ~30 dump commands.
/// </summary>
public sealed class WebAnalyzeReport
{
    /// <summary>Frames beyond this count are evenly downsampled — keeps report size sane for long recordings.</summary>
    private const int MaxFilmstripFrames = 200;

    // Same grouped-replay pattern as TraceAnalyzeCommand.s_traceGroups: a navLevel:2
    // group header followed by each member's own navLevel:3 header (already baked into
    // its captured doc) — never wrap a replayed doc in BeginDetails/EndDetails, since
    // Header() is how the sidebar nav actually understands chapter boundaries; details
    // wrapping just breaks that without adding anything a nav-level grouping doesn't
    // already give for free.
    private static readonly (string Heading, string[] Names)[] s_webGroups =
    [
        ("Memory & Listeners",       ["web-memory-leak"]),
        ("CPU & Rendering",          ["web-cpu-hotspots", "web-jank"]),
        ("Tasks & GC",               ["web-long-tasks", "web-gc-pressure"]),
        ("Network & Responsiveness", ["web-network", "web-input-latency"]),
    ];

    public void Render(
        string traceFileName,
        IReadOnlyList<Finding> allFindings,
        IReadOnlyDictionary<string, ReportDoc> captured,
        IReadOnlyList<(string Key, string SectionTitle)> sectionOrder,
        IRenderSink sink,
        IReadOnlyList<(long TimestampMs, string Base64Jpeg)>? screenshots = null)
    {
        int score = Math.Max(0, 100 - allFindings.Sum(f => f.Deduction));

        sink.Section("Health Score");
        sink.Explain(
            what: "One score (0–100) summarizing every finding below — memory/listener leaks, CPU hotspots, " +
                  "long tasks, and GC pressure all rolled into a single number so you know at a glance whether " +
                  "this recording needs attention.",
            impact: score switch
            {
                < 40 => "This recording shows strong signals of a real performance problem — likely the same " +
                        "kind that would be visible to a user (freezes, jank, or a slow memory leak).",
                < 70 => "This recording shows some degradation. Worth investigating before it gets worse.",
                _    => "No strong performance signal in this recording.",
            });
        sink.Gauges([("Health Score", score, "/100")], barMax: 100);

        RenderLookHereFirst(allFindings, sink);
        RenderActionQueue(allFindings, sink);
        RenderFilmstrip(screenshots, sink);

        // Grouped replay — each group is its own navLevel:2 chapter in the sidebar;
        // each member replays its own navLevel:3 header underneath it, exactly like
        // TraceAnalyzeCommand's rollup. Anything not in a known group (e.g. a plugin
        // sub-analyzer) falls through to a trailing "Other" group so it's never dropped.
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (heading, names) in s_webGroups)
        {
            var available = names.Where(n => captured.ContainsKey(n)).ToList();
            if (available.Count == 0) continue;

            sink.Header($"{heading} ({available.Count})", traceFileName, navLevel: 2);
            foreach (var name in available)
            {
                grouped.Add(name);
                ReportDocReplay.Replay(captured[name], sink);
            }
        }

        var leftover = sectionOrder.Where(s => captured.ContainsKey(s.Key) && !grouped.Contains(s.Key)).ToList();
        if (leftover.Count > 0)
        {
            sink.Header($"Other ({leftover.Count})", traceFileName, navLevel: 2);
            foreach (var (key, _) in leftover)
                ReportDocReplay.Replay(captured[key], sink);
        }
    }

    // ── "Look here first" — the top 3 ranked findings, surfaced before the full tables ──
    private static void RenderLookHereFirst(IReadOnlyList<Finding> findings, IRenderSink sink)
    {
        var top = ActionQueueBuilder.Build(findings).Take(3).ToList();
        if (top.Count == 0) return;

        sink.Section("Look Here First");
        sink.Explain(
            what: "The highest-priority findings across every analyzer, ranked — read these before the full " +
                  "results below.",
            action: "Each one names the command whose full section has the supporting evidence.");

        foreach (var item in top)
            sink.Alert(
                item.Severity switch
                {
                    FindingSeverity.Critical => AlertLevel.Critical,
                    FindingSeverity.Warning  => AlertLevel.Warning,
                    _                        => AlertLevel.Info,
                },
                $"[{item.Priority}] {item.Headline}",
                item.Detail,
                item.TargetCommand is not null ? $"See: {item.TargetCommand} <trace>" : item.Advice);
    }

    // ── Filmstrip — an auto-playing replay of what was on screen during the recording ──
    private static void RenderFilmstrip(IReadOnlyList<(long TimestampMs, string Base64Jpeg)>? screenshots, IRenderSink sink)
    {
        if (screenshots is null || screenshots.Count == 0) return;

        var frames = screenshots.Count > MaxFilmstripFrames
            ? Downsample(screenshots, MaxFilmstripFrames)
            : screenshots;

        sink.Section("Filmstrip");
        sink.Explain(
            what: "Every screenshot DevTools captured during the recording, played back as a flipbook — a fast " +
                  "visual replay of what the user actually saw.",
            action: "Use the slider to jump to a specific moment, or pause and step through frame by frame.");
        sink.Filmstrip(frames, $"{screenshots.Count:N0} frame(s) captured" +
            (screenshots.Count > MaxFilmstripFrames ? $" — showing {MaxFilmstripFrames} (evenly sampled)" : ""));
    }

    private static IReadOnlyList<(long TimestampMs, string Base64Jpeg)> Downsample(
        IReadOnlyList<(long TimestampMs, string Base64Jpeg)> frames, int target)
    {
        var result = new List<(long, string)>(target);
        for (int i = 0; i < target; i++)
            result.Add(frames[(int)((long)i * (frames.Count - 1) / Math.Max(1, target - 1))]);
        return result;
    }

    private static void RenderActionQueue(IReadOnlyList<Finding> findings, IRenderSink sink)
    {
        var items = ActionQueueBuilder.Build(findings);

        sink.Section("Action Queue", "action-queue");
        sink.Explain(
            what: "Every finding, ranked and bucketed into Now / Next / Watch so you know what to act on first.",
            bullets:
            [
                "Now — investigate immediately; these are driving the score down the most.",
                "Next — worth lining up once Now items are handled.",
                "Watch — lower urgency; revisit if the situation escalates.",
            ]);

        if (items.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "Nothing to queue — no actionable findings.");
            return;
        }

        foreach (var bucket in new[] { ActionBucket.Now, ActionBucket.Next, ActionBucket.Watch })
        {
            var bucketItems = items.Where(i => i.Bucket == bucket).ToList();
            if (bucketItems.Count == 0) continue;

            sink.BeginDetails($"{bucket}  ({bucketItems.Count})", open: bucket != ActionBucket.Watch);
            sink.Table(
                ["Priority", "Score", "Severity", "Category", "Finding", "Command"],
                bucketItems.Select(i => new[]
                {
                    i.Priority,
                    i.Score.ToString(),
                    i.Severity switch { FindingSeverity.Critical => "Critical", FindingSeverity.Warning => "Warning", _ => "Info" },
                    i.Category,
                    i.Headline,
                    i.TargetCommand ?? "—",
                }).ToList());
            sink.EndDetails();
        }
    }
}
