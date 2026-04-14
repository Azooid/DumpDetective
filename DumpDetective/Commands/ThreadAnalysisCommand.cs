using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

// Analyzes managed threads: total counts, lifecycle categories, blocked-thread
// detection, per-thread exceptions, and optional collapsible stack-frame panels.
internal static class ThreadAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective thread-analysis <dump-file> [options]

        Options:
          -s, --stacks          Show top-10 stack frames per thread (collapsible in HTML)
          -b, --blocked-only    Show only threads that appear blocked
          --state <s>           Filter by lifecycle state: blocked|running|dead|all (default: all)
          --name <substr>       Filter by thread name (case-insensitive)
          -o, --output <file>   Write report to file (.html / .md / .txt / .json)
          -h, --help            Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showStacks = false;
        bool blockedOnly = false;
        string? nameFilter = null;
        string? stateFilter = null;
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--stacks" or "-s")
                showStacks = true;
            else if (args[i] is "--blocked-only" or "-b")
                blockedOnly = true;
            else if (args[i] == "--name" && i + 1 < args.Length)
                nameFilter = args[++i];
            else if (args[i] == "--state" && i + 1 < args.Length)
                stateFilter = args[++i].ToLowerInvariant();
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showStacks, blockedOnly, nameFilter, stateFilter));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
                                bool showStacks = false, bool blockedOnly = false,
                                string? nameFilter = null, string? stateFilter = null)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Thread Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var threads     = ctx.Runtime.Threads.ToList();
        var threadNames = BuildThreadNameMap(ctx);

        var toShow = threads.AsEnumerable();
        if (blockedOnly)  toShow = toShow.Where(IsLikelyBlocked);
        // --state filter (overrides --blocked-only if both specified)
        if (stateFilter is "blocked") toShow = threads.AsEnumerable().Where(IsLikelyBlocked);
        else if (stateFilter is "running") toShow = threads.AsEnumerable().Where(t => t.IsAlive && !IsLikelyBlocked(t));
        else if (stateFilter is "dead")    toShow = threads.AsEnumerable().Where(t => !t.IsAlive);
        if (nameFilter is not null)
            toShow = toShow.Where(t =>
                threadNames.TryGetValue(t.ManagedThreadId, out var n) &&
                n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        var toShowList = toShow.ToList();

        int blockedCount = threads.Count(IsLikelyBlocked);

        RenderSummary(sink, threads, blockedCount, threadNames);

        if (toShowList.Count == 0) { sink.Text("No threads match the filter."); return; }

        string sectionTitle = stateFilter is not null and not "all"
            ? $"Threads — state={stateFilter} ({toShowList.Count})"
            : blockedOnly
                ? $"Blocked Threads ({toShowList.Count})"
                : nameFilter is not null
                    ? $"Filtered Threads ({toShowList.Count})"
                    : $"All Threads ({toShowList.Count})";

        if (!showStacks)
            RenderThreadTable(sink, toShowList, threadNames, sectionTitle);
        else
            RenderThreadCards(sink, toShowList, threadNames, sectionTitle);

        RenderExceptionDetails(sink, toShowList);
    }

    // Renders the thread count summary KVs, category breakdown table, and blocking alerts.
    static void RenderSummary(IRenderSink sink,
        List<ClrThread> threads, int blockedCount, Dictionary<int, string> threadNames)
    {
        sink.Section("Thread Summary");
        sink.KeyValues(
        [
            ("Total threads",   threads.Count.ToString("N0")),
            ("Alive",           threads.Count(t => t.IsAlive).ToString("N0")),
            ("Likely blocked",  blockedCount.ToString("N0")),
            ("With exception",  threads.Count(t => t.CurrentException is not null).ToString("N0")),
            ("GC cooperative",  threads.Count(t => t.GCMode == GCMode.Cooperative).ToString("N0")),
            ("Named threads",   threadNames.Count.ToString("N0")),
        ]);

        var categories = threads
            .GroupBy(t => ClassifyThread(t, threadNames))
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (categories.Count > 1)
            sink.Table(["Category", "Count"], categories, "Thread categories");

        if (blockedCount >= threads.Count / 2 && threads.Count > 4)
            sink.Alert(AlertLevel.Critical, $"{blockedCount} of {threads.Count} threads are blocked.",
                advice: "Check for deadlocks — run deadlock-detection for wait-chain analysis.");
        else if (blockedCount > 0)
            sink.Alert(AlertLevel.Warning, $"{blockedCount} thread(s) appear blocked on synchronization primitives.");
    }

    // Renders a compact per-thread table row (no stack frames).
    static void RenderThreadTable(IRenderSink sink,
        List<ClrThread> threads, Dictionary<int, string> threadNames, string sectionTitle)
    {
        sink.Section(sectionTitle);
        var rows = new List<string[]>();
        foreach (var t in threads)
        {
            threadNames.TryGetValue(t.ManagedThreadId, out string? threadName);
            string ex       = FormatException(t.CurrentException);
            string lockInfo = GetLockInfo(t);
            string category = ClassifyThread(t, threadNames);
            rows.Add([
                $"{t.ManagedThreadId}",
                $"{t.OSThreadId}",
                threadName ?? "",
                category,
                t.GCMode.ToString(),
                ex,
                lockInfo,
            ]);
        }
        sink.Table(["Mgd ID", "OS ID", "Thread Name", "Category", "GC Mode", "Exception", "Waiting On"], rows);
    }

    // Renders collapsible per-thread cards with top-10 stack frames each.
    static void RenderThreadCards(IRenderSink sink,
        List<ClrThread> threads, Dictionary<int, string> threadNames, string sectionTitle)
    {
        sink.Section(sectionTitle);
        foreach (var t in threads)
        {
            threadNames.TryGetValue(t.ManagedThreadId, out string? threadName);
            string ex       = FormatException(t.CurrentException);
            string lockInfo = GetLockInfo(t);
            bool   blocked  = IsLikelyBlocked(t);
            string category = ClassifyThread(t, threadNames);

            string detailTitle =
                $"Thread {t.ManagedThreadId}" +
                (threadName is not null ? $" [{threadName}]" : "") +
                $"  OS:{t.OSThreadId}  [{category}]" +
                (blocked         ? "  ⚠ BLOCKED"       : "") +
                (ex.Length > 0   ? $"  ex:{ex}"        : "") +
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

    // Renders exception-detail table for any thread carrying a current exception.
    static void RenderExceptionDetails(IRenderSink sink, List<ClrThread> threads)
    {
        var excThreads = threads.Where(t => t.CurrentException is not null).ToList();
        if (excThreads.Count == 0) return;

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

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Categorizes a thread by its likely role based on name, stack, and state.
    /// </summary>
    static string ClassifyThread(ClrThread t, Dictionary<int, string> names)
    {
        if (names.TryGetValue(t.ManagedThreadId, out string? name) && !string.IsNullOrEmpty(name))
        {
            if (name.Contains("Finalizer",   StringComparison.OrdinalIgnoreCase)) return "Finalizer";
            if (name.Contains("GC",           StringComparison.OrdinalIgnoreCase)) return "GC";
            if (name.Contains("Timer",        StringComparison.OrdinalIgnoreCase)) return "Timer";
            if (name.Contains("ThreadPool",   StringComparison.OrdinalIgnoreCase)) return "ThreadPool";
            if (name.Contains("Background",   StringComparison.OrdinalIgnoreCase)) return "Background";
        }
        try
        {
            var frameMethods = t.EnumerateStackTrace().Take(5).Select(f => f.Method?.Name ?? "").ToList();
            if (frameMethods.Any(m => m.Contains("Finaliz", StringComparison.OrdinalIgnoreCase))) return "Finalizer";
            if (frameMethods.Any(m => m is "GarbageCollect" or "Collect"))                       return "GC";
            if (frameMethods.Any(m => m.Contains("Dispatch", StringComparison.OrdinalIgnoreCase))) return "ThreadPool";
            if (frameMethods.Any(m => m.Contains("Main",     StringComparison.OrdinalIgnoreCase))) return "Main";
            if (frameMethods.Any(m => m.Contains("Request",  StringComparison.OrdinalIgnoreCase))) return "Request Handler";
            if (IsLikelyBlocked(t))                                                               return "Blocked";
        }
        catch { }
        return t.IsAlive ? "Managed" : "Dead";
    }

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

    // Formats a ClrException as "TypeName: Message → InnerType" truncated to 110 chars.
    static string FormatException(ClrException? ex)
    {
        if (ex is null) return "";
        string type = ex.Type?.Name ?? "?";
        string msg  = ex.Message ?? "";
        string result = msg.Length > 0 ? $"{type}: {msg}" : type;
        if (ex.Inner is not null) result += $" → {ex.Inner.Type?.Name ?? "?"}";
        return result.Length > 110 ? result[..107] + "…" : result;
    }

    // Truncates a string to 120 chars with an ellipsis suffix.
    static string TruncateMessage(string s) => s.Length > 120 ? s[..117] + "…" : s;

    // Returns the first blocking frame's method signature, or empty if none found.
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

    // Returns true for method names that indicate thread-blocking primitives.
    static bool IsBlockingFrame(string methodName) =>
        methodName is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                   or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || methodName.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || methodName.Contains("Sleep", StringComparison.OrdinalIgnoreCase);

    // Returns true when the top-10 stack frames contain at least one blocking method.
    internal static bool IsLikelyBlocked(ClrThread t)
    {
        return t.EnumerateStackTrace().Take(10).Any(f =>
            IsBlockingFrame(f.Method?.Name ?? string.Empty));
    }
}
