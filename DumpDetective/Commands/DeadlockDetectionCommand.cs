using DumpDetective.Core;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class DeadlockDetectionCommand
{
    private const string Help = """
        Usage: DumpDetective deadlock-detection <dump-file> [options]

        Options:
          --min-threads <N>    Minimum group size to flag (default: 2)
          -o, --output <file>  Write report to file (.md / .html / .txt)
          -h, --help           Show this help

        Note:
          ClrMD 3.x does not expose lock ownership, so wait-chain cycle detection
          is performed heuristically via stack-frame analysis. Use WinDbg !dlk for
          guaranteed Monitor-level deadlock detection on live data.
        """;

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

        // ── Per-thread: collect top blocking frame and its type key ──────────
        var blocked = new List<(ClrThread Thread, string BlockType, string BlockFrame, List<ClrStackFrame> Frames)>();

        foreach (var t in threads)
        {
            var frames = t.EnumerateStackTrace().Take(10).ToList();
            var bf = frames.FirstOrDefault(f => IsBlockingFrame(f.Method?.Name ?? string.Empty));
            if (bf is null) continue;
            string blockType  = ExtractTypeName(bf.FrameName ?? bf.Method?.Signature ?? "");
            string blockFrame = bf.FrameName ?? bf.Method?.Signature ?? "<unknown>";
            blocked.Add((t, blockType, blockFrame, frames));
        }

        sink.Section("Analysis Summary");
        sink.KeyValues([
            ("Threads total",           threads.Count.ToString("N0")),
            ("Blocked threads",         blocked.Count.ToString("N0")),
            ("Named threads found",     threadNames.Count.ToString("N0")),
            ("Unique blocking types",   blocked.Select(b => b.BlockType).Distinct().Count().ToString("N0")),
        ]);

        if (blocked.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No threads appear blocked on synchronization primitives.");
            return;
        }

        // ── Contention groups — threads waiting on the same type ─────────────
        var groups = blocked
            .GroupBy(b => b.BlockType)
            .Where(g => g.Count() >= minThreads)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (groups.Count > 0)
        {
            // Threads blocked on the SAME type is the classic deadlock fingerprint
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

            // ── Wait-for matrix visualization ─────────────────────────────────
            sink.Section("Suspected Wait-for Table");
            sink.Alert(AlertLevel.Info, "Heuristic wait-for table (stack-based — not lock-ownership verified)",
                "Each row shows a thread stuck on a synchronization type. Cycles in the 'Waiting On' column indicate deadlocks.");
            var matrixRows = groups.SelectMany(g => g.Select(b =>
            {
                threadNames.TryGetValue(b.Thread.ManagedThreadId, out string? n);
                // Show top 2 non-blocking frames as "current activity"
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
        else
        {
            sink.Alert(AlertLevel.Warning,
                $"{blocked.Count} blocked thread(s) — no shared contention type (single-thread contention or I/O wait).");
        }

        // ── All blocked threads table ─────────────────────────────────────────
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

        // ── Per-thread stack details ──────────────────────────────────────────
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

    static bool IsBlockingFrame(string name) =>
        name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
               or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || name.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || name.Contains("Sleep", StringComparison.OrdinalIgnoreCase);

    static string ExtractTypeName(string frame)
    {
        int paren = frame.IndexOf('(');
        string sig = paren > 0 ? frame[..paren] : frame;
        int dot    = sig.LastIndexOf('.');
        return dot > 0 ? sig[..dot] : sig;
    }
}
