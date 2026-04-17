using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class ThreadAnalysisAnalyzer
{
    public ThreadAnalysisData Analyze(DumpContext ctx, bool captureStacks = false)
    {
        var threadNames = BuildThreadNameMap(ctx);
        var threads     = ctx.Runtime.Threads.ToList();

        var infos = new List<ThreadInfo>(threads.Count);
        foreach (var t in threads)
        {
            threadNames.TryGetValue(t.ManagedThreadId, out string? name);
            string category = ClassifyThread(t, threadNames);
            string? ex      = FormatException(t.CurrentException);
            string? lock_   = GetLockInfo(t);
            var frames = captureStacks
                ? t.EnumerateStackTrace().Take(10)
                    .Select(f => f.FrameName ?? f.Method?.Signature ?? "<unknown>")
                    .ToList()
                : (IReadOnlyList<string>)[];

            infos.Add(new ThreadInfo(
                ManagedId:      t.ManagedThreadId,
                OSThreadId:     t.OSThreadId,
                Name:           name,
                IsAlive:        t.IsAlive,
                Category:       category,
                GcMode:         t.GCMode.ToString(),
                Exception:      ex?.Length > 0 ? ex : null,
                LockInfo:       lock_?.Length > 0 ? lock_ : null,
                StackFrames:    frames));
        }

        return new ThreadAnalysisData(
            Threads:            infos,
            TotalCount:         threads.Count,
            AliveCount:         threads.Count(t => t.IsAlive),
            BlockedCount:       threads.Count(IsLikelyBlocked),
            WithExceptionCount: threads.Count(t => t.CurrentException is not null),
            NamedCount:         threadNames.Count);
    }

    internal static Dictionary<int, string> BuildThreadNameMap(DumpContext ctx)
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

    internal static bool IsLikelyBlocked(ClrThread t)
    {
        try
        {
            return t.EnumerateStackTrace().Take(10).Any(f =>
                IsBlockingFrame(f.Method?.Name ?? string.Empty));
        }
        catch { return false; }
    }

    internal static string ClassifyThread(ClrThread t, Dictionary<int, string> names)
    {
        if (names.TryGetValue(t.ManagedThreadId, out string? name) && !string.IsNullOrEmpty(name))
        {
            if (name.Contains("Finalizer", StringComparison.OrdinalIgnoreCase)) return "Finalizer";
            if (name.Contains("GC",        StringComparison.OrdinalIgnoreCase)) return "GC";
            if (name.Contains("Timer",     StringComparison.OrdinalIgnoreCase)) return "Timer";
            if (name.Contains("ThreadPool",StringComparison.OrdinalIgnoreCase)) return "ThreadPool";
        }
        try
        {
            var methods = t.EnumerateStackTrace().Take(5).Select(f => f.Method?.Name ?? "").ToList();
            if (methods.Any(m => m.Contains("Finaliz", StringComparison.OrdinalIgnoreCase))) return "Finalizer";
            if (methods.Any(m => m is "GarbageCollect" or "Collect"))                        return "GC";
            if (methods.Any(m => m.Contains("Dispatch",StringComparison.OrdinalIgnoreCase))) return "ThreadPool";
            if (methods.Any(m => m.Contains("Main",    StringComparison.OrdinalIgnoreCase))) return "Main";
            if (methods.Any(m => m.Contains("Request", StringComparison.OrdinalIgnoreCase))) return "Request Handler";
            if (IsLikelyBlocked(t)) return "Blocked";
        }
        catch { }
        return t.IsAlive ? "Managed" : "Dead";
    }

    internal static string GetLockInfo(ClrThread t)
    {
        try
        {
            var frame = t.EnumerateStackTrace().Take(10)
                .FirstOrDefault(f => IsBlockingFrame(f.Method?.Name ?? string.Empty));
            return frame is not null ? (frame.Method?.Signature ?? frame.FrameName ?? "") : "";
        }
        catch { return ""; }
    }

    internal static string FormatException(ClrException? ex)
    {
        if (ex is null) return "";
        string type   = ex.Type?.Name ?? "?";
        string msg    = ex.Message ?? "";
        string result = msg.Length > 0 ? $"{type}: {msg}" : type;
        if (ex.Inner is not null) result += $" → {ex.Inner.Type?.Name ?? "?"}";
        return result.Length > 110 ? result[..107] + "…" : result;
    }

    private static bool IsBlockingFrame(string methodName) =>
        methodName is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                   or "Acquire" or "WaitAsync" or "GetResult" or "WaitAll" or "WaitAny"
        || methodName.Contains("Wait",  StringComparison.OrdinalIgnoreCase)
        || methodName.Contains("Sleep", StringComparison.OrdinalIgnoreCase);
}
