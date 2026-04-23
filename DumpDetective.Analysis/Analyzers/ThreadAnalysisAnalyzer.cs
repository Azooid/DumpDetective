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
        CommandBase.RunStatus($"Walking stack traces ({threads.Count} threads)...", update =>
        {
            int done         = 0;
            int monBlocked   = 0;
            int indepWaiting = 0;
            foreach (var t in threads)
            {
                done++;
                if ((done & 0xF) == 0)
                    update($"Walking stack traces — {done}/{threads.Count} threads  •  {monBlocked} monitor-blocked  •  {indepWaiting} waiting...");
                threadNames.TryGetValue(t.ManagedThreadId, out string? name);
                var waitKind  = ClassifyWait(t);
                string category = ClassifyThread(t, threadNames);
                string? ex      = FormatException(t.CurrentException);
                string? lock_   = GetLockInfo(t);
                if (waitKind == WaitKind.Monitor)     monBlocked++;
                else if (waitKind == WaitKind.Independent) indepWaiting++;
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
                    WaitKind:       waitKind,
                    StackFrames:    frames));
            }
        });

        return new ThreadAnalysisData(
            Threads:             infos,
            TotalCount:          threads.Count,
            AliveCount:          threads.Count(t => t.IsAlive),
            MonitorBlockedCount: infos.Count(i => i.WaitKind == WaitKind.Monitor),
            IndependentWaitCount:infos.Count(i => i.WaitKind == WaitKind.Independent),
            WithExceptionCount:  threads.Count(t => t.CurrentException is not null),
            NamedCount:          threadNames.Count);
    }

    internal static Dictionary<int, string> BuildThreadNameMap(DumpContext ctx)
    {
        if (!ctx.Heap.CanWalkHeap) return new ThreadNameMap();
        // GetOrCreateAnalysis ensures only ONE heap walk runs even when
        // thread-analysis and deadlock-detection call this concurrently.
        // The second caller blocks until the first finishes, then reuses the result.
        ThreadNameMap? result = null;
        CommandBase.RunStatus("Building thread name map (heap walk)...", update =>
            result = ctx.GetOrCreateAnalysis<ThreadNameMap>(() =>
            {
                var map   = new ThreadNameMap();
                long count = 0;
                var sw     = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (var obj in ctx.Heap.EnumerateObjects())
                    {
                        count++;
                        if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                        {
                            update($"Building thread name map — {count:N0} objects scanned  •  {map.Count} thread names found...");
                            sw.Restart();
                        }
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
            }));
        return result!;
    }

    /// <summary>
    /// Classifies a thread's synchronisation state precisely using type-qualified frame matching.
    /// Returns <see cref="WaitKind.Monitor"/> only for Monitor.Enter/ReliableEnter (lock contention).
    /// Returns <see cref="WaitKind.Independent"/> for WaitHandle/Task/Semaphore waits (normal idle).
    /// Returns <see cref="WaitKind.None"/> for running threads.
    /// </summary>
    internal static WaitKind ClassifyWait(ClrThread t)
    {
        try
        {
            foreach (var f in t.EnumerateStackTrace().Take(20))
            {
                string? tn = f.Method?.Type?.Name;
                string? mn = f.Method?.Name;
                if (tn is null || mn is null) continue;

                WaitKind k = (tn, mn) switch
                {
                    // Monitor — lock contention
                    ("System.Threading.Monitor", "Enter" or "ReliableEnter" or "TryEnter")
                        => WaitKind.Monitor,

                    // Independent idle waits — normal for background workers
                    ("System.Threading.WaitHandle",          "WaitOne" or "WaitAny" or "WaitAll")
                        => WaitKind.Independent,
                    ("System.Threading.ManualResetEventSlim","Wait")
                        => WaitKind.Independent,
                    ("System.Threading.SemaphoreSlim",       "Wait" or "WaitAsync")
                        => WaitKind.Independent,
                    ("System.Threading.Tasks.Task",          "Wait" or "WaitAll" or "WaitAny")
                        => WaitKind.Independent,
                    ("System.Threading.Thread",              "Join")
                        => WaitKind.Independent,
                    ("System.Threading.ReaderWriterLockSlim","EnterReadLock" or "EnterWriteLock" or "EnterUpgradeableReadLock")
                        => WaitKind.Independent,
                    ("System.Threading.Mutex",               "WaitOne")
                        => WaitKind.Independent,
                    ("System.Threading.SpinWait",            "SpinOnce")
                        => WaitKind.Independent,

                    _ => WaitKind.None,
                };

                if (k != WaitKind.None) return k;
            }
        }
        catch { }
        return WaitKind.None;
    }

    /// <summary>Kept for internal use — true only for Monitor-blocked threads (real contention).</summary>
    internal static bool IsLikelyBlocked(ClrThread t) =>
        ClassifyWait(t) == WaitKind.Monitor;

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

            return ClassifyWait(t) switch
            {
                WaitKind.Monitor     => "Monitor-Blocked",   // contended lock — may be deadlock
                WaitKind.Independent => "Waiting",           // normal idle wait — NOT a problem
                _                    => t.IsAlive ? "Managed" : "Dead",
            };
        }
        catch { }
        return t.IsAlive ? "Managed" : "Dead";
    }

    /// <summary>
    /// Returns the blocking frame signature if the thread is waiting on any sync primitive,
    /// prefixed with "[Monitor]" or "[Wait]" to distinguish contention from idle waits.
    /// </summary>
    internal static string GetLockInfo(ClrThread t)
    {
        try
        {
            foreach (var f in t.EnumerateStackTrace().Take(20))
            {
                string? tn = f.Method?.Type?.Name;
                string? mn = f.Method?.Name;
                if (tn is null || mn is null) continue;

                string? prefix = (tn, mn) switch
                {
                    ("System.Threading.Monitor", "Enter" or "ReliableEnter" or "TryEnter")
                        => "[Monitor]",
                    ("System.Threading.WaitHandle",          "WaitOne" or "WaitAny" or "WaitAll")   => "[Wait]",
                    ("System.Threading.ManualResetEventSlim","Wait")                                 => "[Wait]",
                    ("System.Threading.SemaphoreSlim",       "Wait" or "WaitAsync")                 => "[Wait]",
                    ("System.Threading.Tasks.Task",          "Wait" or "WaitAll" or "WaitAny")      => "[Wait]",
                    ("System.Threading.Thread",              "Join")                                 => "[Wait]",
                    ("System.Threading.ReaderWriterLockSlim","EnterReadLock" or "EnterWriteLock" or "EnterUpgradeableReadLock") => "[Wait]",
                    ("System.Threading.Mutex",               "WaitOne")                             => "[Wait]",
                    _ => null,
                };

                if (prefix is not null)
                {
                    string sig = f.Method?.Signature ?? $"{tn}.{mn}";
                    return $"{prefix} {Truncate(sig, 100)}";
                }
            }
        }
        catch { }
        return "";
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

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;
}
