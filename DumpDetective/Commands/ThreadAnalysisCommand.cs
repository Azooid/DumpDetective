using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

internal static class ThreadAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective thread-analysis <dump-file> [options]

        Options:
          -s, --stacks          Show top-10 stack frames per thread (collapsible in HTML)
          -b, --blocked-only    Show only threads that appear blocked
          -o, --output <file>   Write report to file (.md / .html / .txt)
          -h, --help            Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showStacks = false, blockedOnly = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        foreach (var a in args)
        {
            if (a is "--stacks"       or "-s") showStacks  = true;
            if (a is "--blocked-only" or "-b") blockedOnly = true;
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showStacks, blockedOnly));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showStacks = false, bool blockedOnly = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Thread Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var threads = ctx.Runtime.Threads.ToList();

        // Build ManagedThreadId → thread name by scanning managed Thread objects on heap
        var threadNames = BuildThreadNameMap(ctx);

        var toShow = blockedOnly ? threads.Where(IsLikelyBlocked).ToList() : threads;

        int blockedCount = threads.Count(IsLikelyBlocked);

        sink.Section("Thread Summary");
        sink.KeyValues(
        [
            ("Total threads",   threads.Count.ToString("N0")),
            ("Likely blocked",  blockedCount.ToString("N0")),
            ("With exception",  threads.Count(t => t.CurrentException is not null).ToString("N0")),
            ("GC cooperative",  threads.Count(t => t.GCMode == GCMode.Cooperative).ToString("N0")),
            ("Named threads",   threadNames.Count.ToString("N0")),
        ]);

        if (blockedCount >= threads.Count / 2 && threads.Count > 4)
            sink.Alert(AlertLevel.Critical, $"{blockedCount} of {threads.Count} threads are blocked.",
                advice: "Check for deadlocks — run deadlock-detection for wait-chain analysis.");
        else if (blockedCount > 0)
            sink.Alert(AlertLevel.Warning, $"{blockedCount} thread(s) appear blocked on synchronization primitives.");

        if (toShow.Count == 0) { sink.Text("No threads match the filter."); return; }

        string sectionTitle = blockedOnly
            ? $"Blocked Threads ({toShow.Count})"
            : $"All Threads ({toShow.Count})";

        if (!showStacks)
        {
            sink.Section(sectionTitle);
            var rows = new List<string[]>();
            foreach (var t in toShow)
            {
                threadNames.TryGetValue(t.ManagedThreadId, out string? threadName);
                string ex       = FormatException(t.CurrentException);
                string lockInfo = GetLockInfo(t);
                rows.Add([
                    $"{t.ManagedThreadId}",
                    $"{t.OSThreadId}",
                    threadName ?? "",
                    t.State.ToString(),
                    t.GCMode.ToString(),
                    ex,
                    lockInfo,
                ]);
            }
            sink.Table(["Mgd ID", "OS ID", "Thread Name", "State", "GC Mode", "Exception", "Waiting On"], rows);
        }
        else
        {
            sink.Section(sectionTitle);
            foreach (var t in toShow)
            {
                threadNames.TryGetValue(t.ManagedThreadId, out string? threadName);
                string ex       = FormatException(t.CurrentException);
                string lockInfo = GetLockInfo(t);
                bool   blocked  = IsLikelyBlocked(t);

                string detailTitle =
                    $"Thread {t.ManagedThreadId}" +
                    (threadName is not null ? $" [{threadName}]" : "") +
                    $"  OS:{t.OSThreadId}  {t.State}" +
                    (blocked   ? "  ⚠ BLOCKED"       : "") +
                    (ex.Length > 0   ? $"  ex:{ex}"  : "") +
                    (lockInfo.Length > 0 ? $"  {lockInfo}" : "");

                sink.BeginDetails(detailTitle, open: blocked || t.CurrentException is not null);

                var frames = t.EnumerateStackTrace().Take(10).ToList();
                if (frames.Count == 0)
                {
                    sink.Text("  (no managed frames)");
                }
                else
                {
                    var frameRows = frames
                        .Select((f, i) => new[]
                        {
                            i.ToString(),
                            f.FrameName ?? f.Method?.Signature ?? f.ToString() ?? "<unknown>",
                        })
                        .ToList();
                    sink.Table(["#", "Frame"], frameRows);
                }

                sink.EndDetails();
            }
        }

        // Exception details section for threads carrying exceptions
        var excThreads = toShow.Where(t => t.CurrentException is not null).ToList();
        if (excThreads.Count > 0)
        {
            sink.Section("Exception Details");
            var exRows = new List<string[]>();
            foreach (var t in excThreads)
            {
                var ex = t.CurrentException!;
                exRows.Add([
                    $"{t.ManagedThreadId}",
                    ex.Type?.Name ?? "?",
                    TruncateMessage(ex.Message ?? ""),
                    ex.Inner?.Type?.Name ?? "",
                ]);
            }
            sink.Table(["Mgd ID", "Exception Type", "Message", "Inner Exception"], exRows);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the heap for System.Threading.Thread objects and maps
    /// ManagedThreadId → name. This is the only reliable way to get thread
    /// names from a managed dump.
    /// </summary>
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

    static string FormatException(ClrException? ex)
    {
        if (ex is null) return "";
        string type = ex.Type?.Name ?? "?";
        string msg  = ex.Message ?? "";
        string result = msg.Length > 0 ? $"{type}: {msg}" : type;
        if (ex.Inner is not null) result += $" → {ex.Inner.Type?.Name ?? "?"}";
        return result.Length > 110 ? result[..107] + "…" : result;
    }

    static string TruncateMessage(string s) => s.Length > 120 ? s[..117] + "…" : s;

    static string GetLockInfo(ClrThread t)
    {
        // ClrMD 3.x does not expose BlockingObjects — derive from top blocking frame
        try
        {
            var frame = t.EnumerateStackTrace().Take(10)
                         .FirstOrDefault(f => IsBlockingFrame(f.Method?.Name ?? string.Empty));
            return frame is not null
                ? (frame.Method?.Signature ?? frame.FrameName ?? "")
                : "";
        }
        catch { return ""; }
    }

    static bool IsBlockingFrame(string methodName) =>
        methodName is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                   or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || methodName.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || methodName.Contains("Sleep", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLikelyBlocked(ClrThread t)
    {
        return t.EnumerateStackTrace().Take(10).Any(f =>
            IsBlockingFrame(f.Method?.Name ?? string.Empty));
    }
}
