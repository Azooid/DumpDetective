using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TimerLeaksCommand
{
    private const string Help = """
        Usage: DumpDetective timer-leaks <dump-file> [options]

        Options:
          -a, --addresses    Show per-timer detail with GC roots, call stacks, and module (up to 200)
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help

        Notes:
          --addresses shows per-timer details including:
            • GC root kind (what is keeping the timer alive)
            • For stack-rooted timers: the full managed call stack of the holding thread
            • Any threads actively executing the callback at the time of the dump
          Most timers are held by Strong Handle (the TimerQueue) — in that case the
          Module (DLL) column identifies the registering assembly.
        """;

    private static readonly HashSet<string> TimerTypeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Threading.TimerQueueTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Windows.Forms.Timer",
    };

    private sealed record TimerInfo(
        string Type, ulong Addr, long Size,
        string Callback, string Module,
        long DueMs, long PeriodMs);

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Timer Leak Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // ── Phase 1: collect timer objects ────────────────────────────────────
        var timers = new List<TimerInfo>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning timer objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!TimerTypeSet.Contains(obj.Type.Name ?? string.Empty)) continue;

                var (cb, module) = ResolveCallback(obj, ctx.Runtime);
                timers.Add(new TimerInfo(
                    obj.Type.Name!, obj.Address, (long)obj.Size,
                    cb, module,
                    ReadTimerLong(obj, "_dueTime"),
                    ReadTimerLong(obj, "_period")));
            }
        });

        sink.Section("Summary");
        if (timers.Count == 0) { sink.Text("No timer objects found."); return; }

        long totalSize = timers.Sum(t => t.Size);
        sink.KeyValues([
            ("Total timer objects", timers.Count.ToString("N0")),
            ("Total size",          DumpHelpers.FormatSize(totalSize)),
        ]);

        if (timers.Count > 500)
            sink.Alert(AlertLevel.Critical, $"{timers.Count:N0} timer objects on heap.",
                advice: "Dispose System.Timers.Timer when no longer needed. Prefer System.Threading.PeriodicTimer (auto-disposing).");
        else if (timers.Count > 100)
            sink.Alert(AlertLevel.Warning, $"{timers.Count:N0} timer objects detected.");

        // ── Phase 2: thread stack collection + GC root tracing ────────────────
        // addrToRoots:     timerAddr → list of (RootKind, Detail, StackFrames)
        // callbackThreads: callbackMethod → threads actively running it right now
        var addrToRoots     = new Dictionary<ulong, List<(string Kind, string Detail, List<string> Frames)>>();
        var callbackThreads = new Dictionary<string, List<(int MgdId, uint OsId, List<string> Frames)>>(StringComparer.Ordinal);

        if (showAddr)
        {
            var timerAddrs  = timers.Select(t => t.Addr).ToHashSet();
            var callbackSet = timers.Where(t => t.Callback.Length > 0)
                                    .Select(t => t.Callback)
                                    .ToHashSet(StringComparer.Ordinal);

            // Pre-collect frames for every alive thread, and detect active callback execution
            var framesByThread = new Dictionary<int, (ClrThread Thread, List<string> Frames)>();
            var threadRanges   = new List<(ClrThread Thread, ulong Lo, ulong Hi)>();

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Collecting thread stacks...", _ =>
            {
                foreach (var thread in ctx.Runtime.Threads.Where(t => t.IsAlive))
                {
                    var frames = thread.EnumerateStackTrace()
                        .Select(f => f.FrameName ?? f.Method?.Signature ?? "")
                        .Where(f => f.Length > 0)
                        .Take(40)
                        .ToList();

                    framesByThread[thread.ManagedThreadId] = (thread, frames);

                    if (thread.StackBase != 0 && thread.StackLimit != 0)
                        threadRanges.Add((thread,
                            Math.Min(thread.StackBase, thread.StackLimit),
                            Math.Max(thread.StackBase, thread.StackLimit)));

                    if (frames.Count == 0) continue;

                    // Check if this thread is currently running any known timer callback
                    foreach (var cb in callbackSet)
                    {
                        string methodShort = cb.Contains('.') ? cb[(cb.LastIndexOf('.') + 1)..] : cb;
                        if (!frames.Any(f => f.Contains(methodShort, StringComparison.OrdinalIgnoreCase))) continue;

                        if (!callbackThreads.TryGetValue(cb, out var tlist))
                        {
                            tlist = [];
                            callbackThreads[cb] = tlist;
                        }
                        if (!tlist.Any(x => x.MgdId == thread.ManagedThreadId))
                            tlist.Add((thread.ManagedThreadId, thread.OSThreadId, frames));
                    }
                }
            });

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Tracing GC roots for timers...", _ =>
            {
                foreach (var root in ctx.Heap.EnumerateRoots())
                {
                    if (!timerAddrs.Contains(root.Object)) continue;

                    string kind = root.RootKind switch
                    {
                        ClrRootKind.Stack             => "Stack",
                        ClrRootKind.StrongHandle      => "Strong Handle",
                        ClrRootKind.PinnedHandle      => "Pinned Handle",
                        ClrRootKind.AsyncPinnedHandle  => "Async-Pinned",
                        ClrRootKind.RefCountedHandle   => "RefCount Handle",
                        ClrRootKind.FinalizerQueue     => "Finalizer Queue",
                        _                              => root.RootKind.ToString(),
                    };

                    string       detail = "";
                    List<string> frames = [];

                    if (root.RootKind == ClrRootKind.Stack && root.Address != 0)
                    {
                        foreach (var (thread, lo, hi) in threadRanges)
                        {
                            if (root.Address < lo || root.Address > hi) continue;
                            detail = $"Thread {thread.ManagedThreadId} (OS: 0x{thread.OSThreadId:X})";
                            if (framesByThread.TryGetValue(thread.ManagedThreadId, out var tf))
                                frames = tf.Frames;
                            break;
                        }
                        if (detail.Length == 0) detail = $"slot 0x{root.Address:X}";
                    }
                    else if (root.Address != 0)
                    {
                        detail = $"handle 0x{root.Address:X}";
                    }

                    if (!addrToRoots.TryGetValue(root.Object, out var rlist))
                    {
                        rlist = [];
                        addrToRoots[root.Object] = rlist;
                    }
                    rlist.Add((kind, detail, frames));
                }
            });
        }

        // ── Callback summary table by type ────────────────────────────────────
        foreach (var g in timers.GroupBy(t => t.Type).OrderByDescending(g => g.Count()))
        {
            long grpSize = g.Sum(t => t.Size);
            sink.BeginDetails($"{g.Key}  —  {g.Count():N0} instance(s)  |  {DumpHelpers.FormatSize(grpSize)}", open: g.Count() > 10);
            var cbGroups = g
                .GroupBy(t => t.Callback.Length > 0 ? t.Callback : "<unknown>")
                .OrderByDescending(cg => cg.Count())
                .Select(cg => new[]
                {
                    cg.Key,
                    cg.First().Module.Length > 0 ? cg.First().Module : "—",
                    cg.Count().ToString("N0"),
                    FormatInterval(cg.First().PeriodMs),
                    FormatInterval(cg.First().DueMs),
                })
                .ToList();
            sink.Table(["Callback Method", "Module (DLL)", "Count", "Period", "Due In"], cbGroups);
            sink.EndDetails();
        }

        // ── Per-timer detail with GC roots + call stacks ──────────────────────
        if (showAddr)
        {
            sink.Section("Per-Timer Detail & Call Stacks");
            sink.Alert(AlertLevel.Info,
                "Root Kind shows what keeps each timer alive. Strong Handle = TimerQueue holds it (normal). " +
                "Use Module (DLL) to identify the registering assembly. " +
                "Stack-rooted timers show the holding thread's full call stack. " +
                "Timers marked ▶ EXECUTING have a thread actively running the callback right now.");

            int idx = 0;
            foreach (var t in timers.OrderBy(x => x.PeriodMs).Take(200))
            {
                idx++;
                addrToRoots.TryGetValue(t.Addr, out var roots);
                callbackThreads.TryGetValue(t.Callback, out var activeThreads);
                bool noRoot      = roots is null || roots.Count == 0;
                bool isExecuting = activeThreads is { Count: > 0 };
                string rootSummary = noRoot
                    ? "No known root"
                    : string.Join(", ", roots!.Select(r => r.Kind).Distinct());

                sink.BeginDetails(
                    $"#{idx}  {t.Type}  @ 0x{t.Addr:X16}  [{rootSummary}]" + (isExecuting ? "  ▶ EXECUTING" : ""),
                    open: noRoot || isExecuting);

                sink.KeyValues([
                    ("Type",           t.Type),
                    ("Address",        $"0x{t.Addr:X16}"),
                    ("Size",           DumpHelpers.FormatSize(t.Size)),
                    ("Period",         FormatInterval(t.PeriodMs)),
                    ("Due In",         FormatInterval(t.DueMs)),
                    ("Callback",       t.Callback.Length > 0 ? t.Callback : "—"),
                    ("Module (DLL)",   t.Module.Length > 0 ? t.Module : "—"),
                    ("GC Root",        rootSummary),
                    ("Active threads", isExecuting ? activeThreads!.Count.ToString() : "0"),
                ]);

                // GC roots — with stack frames where available
                if (!noRoot)
                {
                    foreach (var (kind, detail, frames) in roots!)
                    {
                        if (frames.Count > 0)
                        {
                            var frameRows = frames
                                .Select((f, i) => new[] { i.ToString(), f })
                                .ToList();
                            sink.Table(["#", "Stack Frame"],
                                frameRows,
                                $"Root: {kind}  —  {detail}  (call stack of holding thread)");
                        }
                        else
                        {
                            sink.Text($"  Root: {kind}  —  {(detail.Length > 0 ? detail : "—")}");
                            if (kind == "Strong Handle")
                                sink.Text("  → Held by the TimerQueue (static). No thread stack available — check Module (DLL) to identify the leak source.");
                        }
                    }
                }
                else
                {
                    sink.Alert(AlertLevel.Warning, "No GC root found — timer may be unreachable and awaiting finalizer/collection.");
                }

                // Threads actively running the callback right now
                if (isExecuting)
                {
                    string methodShort = t.Callback.Contains('.') ? t.Callback[(t.Callback.LastIndexOf('.') + 1)..] : t.Callback;
                    foreach (var (mgdId, osId, frames) in activeThreads!)
                    {
                        var frameRows = frames
                            .Select((f, i) =>
                            {
                                bool hot = f.Contains(methodShort, StringComparison.OrdinalIgnoreCase);
                                return new[] { i.ToString(), hot ? "►" : " ", f };
                            })
                            .ToList();
                        sink.Table(
                            ["#", "", "Stack Frame"],
                            frameRows,
                            $"Thread {mgdId} (OS: 0x{osId:X}) is actively executing this callback  (► = callback frame)");
                    }
                }

                sink.EndDetails();
            }

            if (timers.Count > 200)
                sink.Alert(AlertLevel.Info, $"Showing first 200 of {timers.Count:N0} timer objects (ordered by period).");
        }

        // ── Period frequency buckets ──────────────────────────────────────────
        sink.Section("Timer Period Distribution");
        var periodBuckets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in timers)
        {
            string bucket = t.PeriodMs switch
            {
                -1          => "Infinite (one-shot)",
                0           => "0 ms (immediate)",
                < 100       => "< 100 ms (high-frequency)",
                < 1_000     => "100 ms – 1 s",
                < 10_000    => "1 s – 10 s",
                < 60_000    => "10 s – 1 min",
                < 3_600_000 => "1 min – 1 hr",
                _           => "> 1 hr",
            };
            periodBuckets[bucket] = periodBuckets.GetValueOrDefault(bucket, 0) + 1;
        }
        var periodRows = periodBuckets
            .OrderBy(kv => PeriodBucketSortKey(kv.Key))
            .Select(kv => new[] { kv.Key, kv.Value.ToString("N0") })
            .ToList();
        sink.Table(["Period Range", "Count"], periodRows, "High-frequency timers cause CPU overhead");

        int highFreqCount = timers.Count(t => t.PeriodMs >= 0 && t.PeriodMs < 100);
        if (highFreqCount > 10)
            sink.Alert(AlertLevel.Warning,
                $"{highFreqCount} timer(s) firing more than 10×/sec (period < 100 ms).",
                "High-frequency timers create CPU and GC pressure.",
                "Consolidate short-interval timers into a single dispatcher or use System.Threading.PeriodicTimer.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static (string Callback, string Module) ResolveCallback(ClrObject obj, ClrRuntime runtime)
    {
        try
        {
            var cb = obj.ReadObjectField("m_callback");
            if (cb.IsNull || !cb.IsValid) return ("", "");
            ulong ptr = cb.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return (cb.Type?.Name ?? "", "");
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return (cb.Type?.Name ?? "", "");
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            string method   = $"{typePart}{m.Name}";
            string module   = Path.GetFileName(m.Type?.Module?.Name ?? "");
            return (method, module);
        }
        catch { return ("", ""); }
    }

    static long ReadTimerLong(ClrObject obj, string fieldName)
    {
        try { return obj.ReadField<long>(fieldName); } catch { }
        try { return obj.ReadField<int>(fieldName); }  catch { }
        return -1;
    }

    static string FormatInterval(long ms) => ms switch
    {
        -1       => "∞",
        0        => "0 ms",
        < 1_000  => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:F1} s",
        _        => $"{ms / 60_000.0:F1} min",
    };

    static int PeriodBucketSortKey(string b) => b switch
    {
        "Infinite (one-shot)"       => 0,
        "0 ms (immediate)"          => 1,
        "< 100 ms (high-frequency)" => 2,
        "100 ms – 1 s"              => 3,
        "1 s – 10 s"                => 4,
        "10 s – 1 min"              => 5,
        "1 min – 1 hr"              => 6,
        "> 1 hr"                    => 7,
        _                           => 8,
    };
}
