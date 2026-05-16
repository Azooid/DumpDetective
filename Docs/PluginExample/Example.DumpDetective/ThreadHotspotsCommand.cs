using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

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

        var runtime = ctx.Runtime;
        var threads = runtime.Threads;

        // Collect per-thread: OS thread ID + managed stack frames
        var threadStacks = new List<(ClrThread Thread, List<string> Frames)>();

        foreach (var thread in threads)
        {
            var frames = new List<string>();
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false))
            {
                string? method = frame.Method?.Signature ?? frame.Method?.Name;
                if (method is not null)
                    frames.Add(method);
            }
            threadStacks.Add((thread, frames));
        }

        int totalThreads   = threadStacks.Count;
        int managedThreads = threadStacks.Count(t => t.Frames.Count > 0);
        int nativeOnly     = totalThreads - managedThreads;

        sink.KeyValues(
        [
            ("Total threads",           totalThreads.ToString("N0")),
            ("With managed frames",     managedThreads.ToString("N0")),
            ("Native-only / GC helper", nativeOnly.ToString("N0")),
        ]);

        // Group by topmost managed frame
        var groups = threadStacks
            .Where(t => t.Frames.Count > 0)
            .GroupBy(t => t.Frames[0], StringComparer.Ordinal)
            .Where(g => g.Count() >= minCount)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .ToList();

        if (groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No hotspots found.",
                $"No group of {minCount}+ threads shares the same topmost frame.");
            return;
        }

        int hotspotThreads = groups.Sum(g => g.Count());
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
                g.Count().ToString("N0"),
                totalThreads > 0 ? $"{g.Count() * 100.0 / totalThreads:F1} %" : "—",
                g.Key,
            }).ToList(),
            caption: $"Top {groups.Count} group(s) with ≥{minCount} threads each");

        if (!details) return;

        // Detailed per-group stack traces
        sink.Section("Detailed Stack Traces per Group");
        foreach (var g in groups)
        {
            sink.BeginDetails($"{g.Count()} thread(s) at: {Truncate(g.Key, 80)}", open: false);
            foreach (var (thread, frames) in g)
            {
                sink.Text($"Thread OSId=0x{thread.OSThreadId:X}  ManagedId={thread.ManagedThreadId}");
                foreach (var frame in frames)
                    sink.Text($"  {frame}");
                sink.BlankLine();
            }
            sink.EndDetails();
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 1), "…");
}
