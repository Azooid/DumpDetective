using DumpDetective.Core;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Detects potential deadlocks by scanning thread stacks for blocking synchronization calls
// and grouping threads that are waiting on the same lock type. Because ClrMD 3.x does not
// expose lock ownership, the analysis is heuristic (stack-frame based), not definitive.
internal static class DeadlockDetectionCommand
{
    private const string Help = """
        Usage: DumpDetective deadlock-detection <dump-file> [options]

        Options:
          --min-threads <N>    Minimum group size to flag (default: 2)
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Note:
          ClrMD 3.x does not expose lock ownership, so wait-chain cycle detection
          is performed heuristically via stack-frame analysis. Use WinDbg !dlk for
          guaranteed Monitor-level deadlock detection on live data.
        """;

    // A managed thread that is currently sitting on a blocking stack frame.
    private sealed record BlockedEntry(
        ClrThread            Thread,
        string               BlockType,   // the type name extracted from the blocking frame (used as contention key)
        string               BlockFrame,  // full frame signature of the topmost blocking call
        List<ClrStackFrame>  Frames);     // up to 10 frames sampled from the thread's stack

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        int minThreads = 2;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--min-threads" && i + 1 < args.Length)
                int.TryParse(args[++i], out minThreads);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, minThreads));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, int minThreads = 2)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Deadlock Detection",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var threads     = ctx.Runtime.Threads.ToList();
        var threadNames = BuildThreadNameMap(ctx);
        var blocked     = ScanBlockedThreads(threads);
        var groups      = BuildContentionGroups(blocked, minThreads);

        RenderSummary(sink, threads, blocked, threadNames);

        if (blocked.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No threads appear blocked on synchronization primitives.");
            return;
        }

        if (groups.Count > 0)
        {
            RenderContentionGroups(sink, groups, threadNames);
            RenderWaitForTable(sink, groups, threadNames);
        }
        else
        {
            sink.Alert(AlertLevel.Warning,
                $"{blocked.Count} blocked thread(s) — no shared contention type (single-thread contention or I/O wait).");
        }

        RenderAllBlocked(sink, blocked, groups, threadNames);
        RenderStackDetails(sink, blocked, groups, threadNames);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Iterates every managed thread and keeps those whose top 10 frames contain a known
    // blocking call. The topmost blocking frame is used as the contention key.
    static List<BlockedEntry> ScanBlockedThreads(List<ClrThread> threads)
    {
        var blocked = new List<BlockedEntry>();
        foreach (var t in threads)
        {
            var frames = t.EnumerateStackTrace().Take(10).ToList();
            var bf = frames.FirstOrDefault(f => IsBlockingFrame(f.Method?.Name ?? string.Empty));
            if (bf is null) continue;
            string blockType  = ExtractTypeName(bf.FrameName ?? bf.Method?.Signature ?? "");
            string blockFrame = bf.FrameName ?? bf.Method?.Signature ?? "<unknown>";
            blocked.Add(new BlockedEntry(t, blockType, blockFrame, frames));
        }
        return blocked;
    }

    // Groups blocked threads by their BlockType (the declaring type of the blocking call).
    // Only groups with at least minThreads members are returned — a group of 2+ threads
    // waiting on the same type is the primary heuristic indicator of a deadlock.
    static List<IGrouping<string, BlockedEntry>> BuildContentionGroups(
        List<BlockedEntry> blocked, int minThreads) =>
        blocked
            .GroupBy(b => b.BlockType)
            .Where(g => g.Count() >= minThreads)
            .OrderByDescending(g => g.Count())
            .ToList();

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Key-value overview: total threads, blocked count, named-thread count, unique lock types.
    static void RenderSummary(
        IRenderSink sink,
        List<ClrThread> threads,
        List<BlockedEntry> blocked,
        Dictionary<int, string> threadNames)
    {
        sink.Section("Analysis Summary");
        sink.KeyValues([
            ("Threads total",           threads.Count.ToString("N0")),
            ("Blocked threads",         blocked.Count.ToString("N0")),
            ("Named threads found",     threadNames.Count.ToString("N0")),
            ("Unique blocking types",   blocked.Select(b => b.BlockType).Distinct().Count().ToString("N0")),
        ]);
    }

    // Critical alert + contention-group table. Each row names the lock type, how many
    // threads are waiting on it, their IDs, and the actual blocking frame signature.
    static void RenderContentionGroups(
        IRenderSink sink,
        List<IGrouping<string, BlockedEntry>> groups,
        Dictionary<int, string> threadNames)
    {
        sink.Alert(AlertLevel.Critical,
            $"{groups.Count} contention group(s) — {groups.Sum(g => g.Count())} threads blocked on the same type.",
            "Multiple threads waiting on the same lock type is the primary deadlock indicator.",
            "Enforce lock ordering, use SemaphoreSlim with CancellationToken timeouts, or convert to async/await.");

        sink.Section("Contention Groups");
        var groupRows = groups.Select(g =>
        {
            var threadIds = g.Select(b =>
            {
                threadNames.TryGetValue(b.Thread.ManagedThreadId, out string? n);
                return n is not null ? $"T{b.Thread.ManagedThreadId}[{n}]" : $"T{b.Thread.ManagedThreadId}";
            });
            return new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                string.Join(", ", threadIds),
                g.First().BlockFrame,
            };
        }).ToList();
        sink.Table(["Block Type", "Thread Count", "Thread IDs", "Block Frame"], groupRows,
            "Groups of 2+ threads blocked on the same primitive — potential deadlock");
    }

    // Heuristic wait-for matrix: one row per thread in a contention group showing which
    // type it is blocked on and the two non-blocking frames that give context for what it
    // was doing before it blocked.
    static void RenderWaitForTable(
        IRenderSink sink,
        List<IGrouping<string, BlockedEntry>> groups,
        Dictionary<int, string> threadNames)
    {
        sink.Section("Suspected Wait-for Table");
        sink.Alert(AlertLevel.Info, "Heuristic wait-for table (stack-based — not lock-ownership verified)",
            "Each row shows a thread stuck on a synchronization type. Cycles in the 'Waiting On' column indicate deadlocks.");

        var matrixRows = groups.SelectMany(g => g.Select(b =>
        {
            threadNames.TryGetValue(b.Thread.ManagedThreadId, out string? n);
            // Show top 2 non-blocking frames as contextual "current activity"
            var contextFrames = b.Frames
                .Where(f => !IsBlockingFrame(f.Method?.Name ?? ""))
                .Take(2)
                .Select(f => f.Method?.Name ?? f.FrameName ?? "?");
            return new[]
            {
                $"T{b.Thread.ManagedThreadId}" + (n is not null ? $" [{n}]" : ""),
                $"OS {b.Thread.OSThreadId}",
                b.BlockType,
                b.BlockFrame.Length > 60 ? b.BlockFrame[..57] + "…" : b.BlockFrame,
                string.Join(" → ", contextFrames),
            };
        })).ToList();
        sink.Table(
            ["Thread", "OS ID", "Waiting On (Type)", "Block Frame", "Context Frames"],
            matrixRows,
            "Threads from contention groups only");
    }

    // Flat table of every blocked thread with its blocking frame and a contention flag.
    static void RenderAllBlocked(
        IRenderSink sink,
        List<BlockedEntry> blocked,
        List<IGrouping<string, BlockedEntry>> groups,
        Dictionary<int, string> threadNames)
    {
        sink.Section("All Blocked Threads");
        var blockedRows = blocked.Select((b, i) =>
        {
            threadNames.TryGetValue(b.Thread.ManagedThreadId, out string? n);
            bool inHot = groups.Any(g => g.Any(x => x.Thread.ManagedThreadId == b.Thread.ManagedThreadId));
            return new[]
            {
                (i + 1).ToString(),
                b.Thread.ManagedThreadId.ToString(),
                b.Thread.OSThreadId.ToString(),
                n ?? "",
                b.BlockFrame.Length > 70 ? b.BlockFrame[..67] + "…" : b.BlockFrame,
                inHot ? "⚠ CONTENTION" : "",
            };
        }).ToList();
        sink.Table(["#", "Mgd ID", "OS ID", "Thread Name", "Block Frame", "Flag"], blockedRows);
    }

    // Per-thread collapsible stack panels. Threads in a contention group are expanded by
    // default; others are collapsed to avoid noise.
    static void RenderStackDetails(
        IRenderSink sink,
        List<BlockedEntry> blocked,
        List<IGrouping<string, BlockedEntry>> groups,
        Dictionary<int, string> threadNames)
    {
        sink.Section("Blocked Thread Stack Details");
        foreach (var b in blocked)
        {
            bool inHotGroup = groups.Any(g => g.Any(x => x.Thread.ManagedThreadId == b.Thread.ManagedThreadId));
            threadNames.TryGetValue(b.Thread.ManagedThreadId, out string? n);
            string title = $"Thread {b.Thread.ManagedThreadId}" +
                           (n is not null ? $" [{n}]" : "") +
                           $"  OS:{b.Thread.OSThreadId}" +
                           (inHotGroup ? "  ⚠ CONTENTION GROUP" : "");
            sink.BeginDetails(title, open: inHotGroup);
            var frameRows = b.Frames
                .Select((f, i) => new[] { i.ToString(), f.FrameName ?? f.Method?.Signature ?? "<unknown>" })
                .ToList();
            if (frameRows.Count > 0)
                sink.Table(["#", "Frame"], frameRows);
            else
                sink.Text("  (no managed frames)");
            sink.EndDetails();
        }
    }

    // ── Thread name map ───────────────────────────────────────────────────────

    // Walks the heap for System.Threading.Thread objects and maps managed thread ID → name.
    // Thread names are emitted by application code via Thread.Name and are useful for
    // identifying worker, I/O, or background threads in the report.
    static Dictionary<int, string> BuildThreadNameMap(DumpContext ctx)
    {
        var map = new Dictionary<int, string>();
        if (!ctx.Heap.CanWalkHeap) return map;
        try
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type?.Name != "System.Threading.Thread") continue;
                try
                {
                    int mgdId = obj.ReadField<int>("_managedThreadId");
                    string? name = null;
                    try { name = obj.ReadStringField("_name"); } catch { }
                    if (!string.IsNullOrEmpty(name) && mgdId > 0)
                        map[mgdId] = name!;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    // ── Frame analysis ────────────────────────────────────────────────────────

    // Returns true when the method name is a well-known synchronization blocking call.
    // Covers Monitor, Semaphore, Task, Thread.Join and common Wait/Sleep patterns.
    static bool IsBlockingFrame(string name) =>
        name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
               or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || name.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || name.Contains("Sleep", StringComparison.OrdinalIgnoreCase);

    // Strips the method name from a frame signature and returns the declaring type name,
    // used to group threads that are blocked on the same class of synchronization primitive.
    static string ExtractTypeName(string frame)
    {
        int paren = frame.IndexOf('(');
        string sig = paren > 0 ? frame[..paren] : frame;
        int dot    = sig.LastIndexOf('.');
        return dot > 0 ? sig[..dot] : sig;
    }
}
