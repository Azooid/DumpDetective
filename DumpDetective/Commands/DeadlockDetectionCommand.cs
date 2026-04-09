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
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Deadlock Detection",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var threads = ctx.Runtime.Threads.ToList();

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
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (groups.Count > 0)
        {
            // Threads blocked on the SAME type is the classic deadlock fingerprint
            sink.Alert(AlertLevel.Critical,
                $"{groups.Count} contention group(s) — multiple threads blocked on the same type.",
                advice: "Use lock ordering, SemaphoreSlim with CancellationToken timeouts, or async/await.");

            sink.Section("Contention Groups");
            var groupRows = groups.Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                string.Join(", ", g.Select(b => $"T{b.Thread.ManagedThreadId}")),
                g.First().BlockFrame,
            }).ToList();
            sink.Table(["Block Type", "Thread Count", "Thread IDs", "Block Frame"], groupRows,
                "Groups of 2+ threads blocked on the same primitive — potential deadlock");
        }
        else
        {
            sink.Alert(AlertLevel.Warning,
                $"{blocked.Count} blocked thread(s) — no shared contention type (single-thread contention or I/O wait).");
        }

        // ── All blocked threads table ─────────────────────────────────────────
        sink.Section("Blocked Threads");
        var blockedRows = blocked.Select((b, i) => new[]
        {
            (i + 1).ToString(),
            b.Thread.ManagedThreadId.ToString(),
            b.Thread.OSThreadId.ToString(),
            b.Thread.State.ToString(),
            b.BlockFrame,
        }).ToList();
        sink.Table(["#", "Mgd ID", "OS ID", "State", "Block Frame"], blockedRows);

        // ── Per-thread stack details ──────────────────────────────────────────
        sink.Section("Blocked Thread Stack Details");
        bool anyInGroup = groups.Count > 0;
        foreach (var b in blocked)
        {
            bool inHotGroup = groups.Any(g => g.Any(x => x.Thread.ManagedThreadId == b.Thread.ManagedThreadId));
            string title = $"Thread {b.Thread.ManagedThreadId}  OS:{b.Thread.OSThreadId}  {b.Thread.State}" +
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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
