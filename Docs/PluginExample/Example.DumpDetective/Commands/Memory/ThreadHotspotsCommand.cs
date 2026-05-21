using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using System.Collections.Frozen;

namespace Example.DumpDetective;

/// <summary>
/// Groups all managed threads by their topmost managed stack frame.
/// High counts at a single frame indicate saturation, a blocking call, or a deadlock.
///
/// Use this when thread-pool saturation or blocked threads are suspected.
/// Pass --details to see the full stack trace for each thread in the top groups.
/// </summary>
public sealed class ThreadHotspotsCommand : ICommand
{
    public string Name                 => "thread-hotspots";
    public string Description          => "Group managed threads by topmost stack frame to spot saturation and blocking.";
    public bool   IncludeInFullAnalyze => true;
    public string Category             => "Example Plugin";
    public CommandKind Kind            => CommandKind.Memory;

    private const string Help = """
        Usage: DumpDetective thread-hotspots <dump.dmp> [options]

        Enumerates every managed thread and groups them by their topmost managed stack
        frame.  A large number of threads stuck at the same frame usually indicates:

          • Thread-pool saturation       — all threads waiting for the same resource
          • A blocking call              — sync-over-async, Monitor.Enter, WaitHandle.Wait
          • A deadlock                   — two or more groups each waiting on the other

        Options:
          -n, --top <N>     Show the top N hotspot groups (default: 10)
          --min-count <N>   Only show groups with at least N threads (default: 2)
          --details         Print the full managed stack trace for each thread in every group
          -o, --output <f>  Write report to file (.html / .md / .txt / .json)
          -h, --help        Show this help

        Examples:
          DumpDetective thread-hotspots app.dmp
          DumpDetective thread-hotspots app.dmp --top 5 --details
          DumpDetective thread-hotspots app.dmp --min-count 5
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        var  a        = CliArgs.Parse(args);
        int  top      = a.GetInt("top", 10);
        int  minCount = a.GetInt("min-count", 2);
        bool details  = a.HasFlag("details");
        return CommandBase.Execute(a.DumpPath, a.EffectiveOutputPaths,
            (ctx, sink) => RenderWith(ctx, sink, top, minCount, details));
    }

    public void Render(DumpContext ctx, IRenderSink sink) => RenderWith(ctx, sink, 10, 2, false);

    // ── analysis ─────────────────────────────────────────────────────────────

    private static void RenderWith(DumpContext ctx, IRenderSink sink, int top, int minCount, bool details)
    {
        CommandBase.RenderHeader("Thread Hotspots", ctx, sink);
        var cache = ctx.GetOrCreateAnalysis<ThreadHotspotsCache>(() => ThreadHotspotsCache.Build(ctx));
        var threadNames = ctx.GetAnalysis<ThreadNameMap>();

        var threadStacks = cache.Threads;

        int totalThreads   = threadStacks.Count;
        int managedThreads = threadStacks.Count(thread => thread.TopFrame is not null);
        int nativeOnly     = totalThreads - managedThreads;
        int namedThreads   = threadNames is null ? 0 : threadStacks.Count(thread => threadNames.ContainsKey(thread.ManagedThreadId));

        sink.KeyValues(
        [
            ("Total threads",           totalThreads.ToString("N0")),
            ("With managed frames",     managedThreads.ToString("N0")),
            ("Native-only / GC helper", nativeOnly.ToString("N0")),
            ("Named managed threads",   namedThreads.ToString("N0")),
        ]);

        // Group by topmost managed frame
        var groups = threadStacks
            .Where(thread => thread.TopFrame is not null)
            .GroupBy(thread => thread.TopFrame!, StringComparer.Ordinal)
            .Select(g => (Frame: g.Key, Threads: g.ToList()))
            .Where(g => g.Threads.Count >= minCount)
            .OrderByDescending(g => g.Threads.Count)
            .Take(top)
            .ToList();

        if (groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No hotspots found.",
                $"No group of {minCount}+ threads shares the same topmost frame.");
            return;
        }

        int hotspotThreads = groups.Sum(g => g.Threads.Count);
        double pct = totalThreads > 0 ? hotspotThreads * 100.0 / totalThreads : 0;

        if (pct >= 50)
            sink.Alert(AlertLevel.Warning,
                $"{hotspotThreads} threads ({pct:F0}%) are in hotspot groups",
                "This indicates resource saturation or heavy blocking at a single call site.",
                "Investigate the top frame(s) — look for lock contention, sync-over-async, or I/O blocking.");
        else
            sink.Alert(AlertLevel.Info,
                $"{hotspotThreads} threads ({pct:F0}%) are in hotspot groups",
                "Thread activity appears spread across multiple call sites.");

        sink.Section("Top Thread Hotspot Groups");
        sink.Table(
            ["Threads", "% of total", "Topmost managed frame"],
            groups.Select(g => new[]
            {
                g.Threads.Count.ToString("N0"),
                totalThreads > 0 ? $"{g.Threads.Count * 100.0 / totalThreads:F1} %" : "—",
                g.Frame,
            }).ToList(),
            caption: $"Top {groups.Count} group(s) with ≥{minCount} threads each");

        if (!details) return;

        var detailsCache = ctx.GetOrCreateAnalysis<ThreadHotspotDetailsCache>(() => ThreadHotspotDetailsCache.Build(ctx));
        var detailsByManagedId = detailsCache.Threads.ToFrozenDictionary(t => t.ManagedThreadId);

        // Detailed per-group stack traces
        sink.Section("Detailed Stack Traces per Group");
        foreach (var g in groups)
        {
            sink.BeginDetails($"{g.Threads.Count} thread(s) at: {Truncate(g.Frame, 80)}", open: false);
            foreach (var thread in g.Threads)
            {
                string threadHeader = $"Thread OSId=0x{thread.OsThreadId:X}  ManagedId={thread.ManagedThreadId}";
                if (threadNames is not null && threadNames.TryGetValue(thread.ManagedThreadId, out var threadName))
                    threadHeader += $"  Name={threadName}";

                sink.Text(threadHeader);
                if (detailsByManagedId.TryGetValue(thread.ManagedThreadId, out var fullThread))
                {
                    foreach (var frame in fullThread.Frames)
                        sink.Text($"  {frame}");
                }
                else if (thread.TopFrame is not null)
                {
                    sink.Text($"  {thread.TopFrame}");
                }

                sink.BlankLine();
            }
            sink.EndDetails();
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 1), "…");
}
