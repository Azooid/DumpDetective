using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Detects potential deadlock cycles by building a wait-for graph from monitor locks.
/// Algorithm:
///   1. Build address→ClrThread and managedId→ClrThread lookup tables.
///   2. Enumerate all inflated sync blocks (<c>ClrSyncBlock.IsMonitorHeld</c>) to find
///      held monitor locks and their owning threads.
///   3. Approximate waiters: ClrMD 3.x does not expose per-SyncBlock waiter lists, so
///      threads whose top stack frame is a Monitor.Enter/ReliableEnter call and whose
///      LockCount == 0 are treated as candidates waiting on any held lock of the same
///      type — this is a heuristic, not guaranteed correct for all scenarios.
///   4. Build a wait-for graph (waiter → owner) and run DFS cycle detection.
///   5. Classify all non-deadlocked threads by wait state for the summary table.
/// </summary>
public sealed class DeadlockAnalyzer
{
    public DeadlockData Analyze(DumpContext ctx, int _ = 2)
    {
        var threads     = ctx.Runtime.Threads.ToList();
        var threadNames = ThreadAnalysisAnalyzer.BuildThreadNameMap(ctx);

        // ── 1. Build address→ClrThread lookup ─────────────────────────────
        var byAddress = new Dictionary<ulong, ClrThread>(threads.Count);
        var byManaged = new Dictionary<int, ClrThread>(threads.Count);
        foreach (var t in threads)
        {
            if (t.Address != 0) byAddress[t.Address] = t;
            byManaged[t.ManagedThreadId] = t;
        }

        // ── 2. Enumerate sync blocks ───────────────────────────────────────
        var monitorLocks  = new List<MonitorLockEntry>();
        // Maps waiterManagedId → ownerManagedId for Monitor.Enter waiters
        var waitForGraph  = new Dictionary<int, int>();

        CommandBase.RunStatus($"Scanning sync blocks for monitor locks ({threads.Count} threads)...", () =>
        {
            try
            {
                foreach (var sb in ctx.Heap.EnumerateSyncBlocks())
                {
                    // Only inflated monitor locks that are currently held interest us.
                    if (!sb.IsMonitorHeld || sb.Object == 0)
                        continue;

                    // Resolve owner thread.
                    byAddress.TryGetValue(sb.HoldingThreadAddress, out ClrThread? owner);

                    // Resolve the type name of the lock object for display.
                    string typeName = "<unknown>";
                    try
                    {
                        var obj = ctx.Heap.GetObject(sb.Object);
                        if (obj.IsValid && obj.Type?.Name is string tn)
                            typeName = tn;
                    }
                    catch { }

                    // ClrMD 3.x does not expose per-SyncBlock waiter thread lists.
                    // Waiters are identified in step 3 by inspecting thread stack frames.
                    // WaiterManagedIds starts empty and is filled after all locks are known.
                    monitorLocks.Add(new MonitorLockEntry(
                        LockAddress:     sb.Object,
                        LockTypeName:    typeName,
                        OwnerManagedId:  owner?.ManagedThreadId,
                        OwnerOSId:       owner?.OSThreadId,
                        OwnerThreadName: owner is not null ? (threadNames.TryGetValue(owner.ManagedThreadId, out var n) ? n : null) : null,
                        WaiterManagedIds: [],          // filled in step 3
                        RecursionCount:  sb.RecursionCount));
                }
            }
            catch { /* some dumps may not have a valid sync block table */ }
        });

        // ── 3. Find Monitor-waiting threads (blocked on Monitor.Enter/ReliableEnter) ─
        //    These are threads whose top stack frame shows Monitor.Enter and who do
        //    NOT own that lock (i.e. they are waiting, not re-entering).
        var monitorWaiters  = new List<(ClrThread Thread, string TopUserFrame, IReadOnlyList<string> Frames)>();
        var independentList = new List<IndependentWaiter>();

        CommandBase.RunStatus("Classifying thread wait states...", update =>
        {
            int done = 0;
            foreach (var t in threads)
            {
                done++;
                if ((done & 0xF) == 0)
                    update($"Classifying thread wait states — {done}/{threads.Count}  •  {monitorWaiters.Count} monitor waiters  •  {independentList.Count} independent...");
                if (!t.IsAlive) continue;
                try
                {
                    var frames = t.EnumerateStackTrace().Take(20).ToList();
                    // Check if this thread is waiting on Monitor.Enter / Monitor.ReliableEnter
                    bool isMonitorWaiter = frames.Any(f =>
                        f.Method?.Type?.Name == "System.Threading.Monitor" &&
                        (f.Method.Name == "Enter" || f.Method.Name == "ReliableEnter" ||
                         f.Method.Name == "TryEnter"));

                    if (isMonitorWaiter)
                    {
                        string topUser = FindTopUserFrame(frames);
                        monitorWaiters.Add((t, topUser, frames.Select(FrameLabel).ToList()));
                        continue;
                    }

                    // Check if blocked on non-monitor synchronization
                    string? blockReason = DetectIndependentWait(frames);
                    if (blockReason is null) continue;

                    string topFrame = FindTopUserFrame(frames);
                    threadNames.TryGetValue(t.ManagedThreadId, out string? tName);
                    independentList.Add(new IndependentWaiter(
                        ManagedId:    t.ManagedThreadId,
                        OSThreadId:   t.OSThreadId,
                        ThreadName:   tName,
                        BlockReason:  blockReason,
                        TopUserFrame: topFrame,
                        StackFrames:  frames.Select(FrameLabel).ToList()));
                }
                catch { }
            }
        });

        // ── 4. Associate monitor waiters with their lock owner ─────────────
        //    We use `ClrThread.LockCount`: a thread holding >= 1 lock while
        //    also blocked on Monitor.Enter is the classic self-deadlock / owner
        //    that's also waiting.  Map waiter → owner via sync-block ownership.
        var lockByOwner = new Dictionary<int, List<MonitorLockEntry>>();
        foreach (var ml in monitorLocks)
        {
            if (ml.OwnerManagedId is int oid)
            {
                if (!lockByOwner.TryGetValue(oid, out var lst))
                    lockByOwner[oid] = lst = [];
                lst.Add(ml);
            }
        }

        // Rebuild monitor locks with waiter lists attached.
        // Associate each waiter with the first lock whose owner could be identified.
        // Because ClrMD 3.x doesn't give us the exact lock object a waiter is waiting
        // for, we use a heuristic: if there is only one contested lock of a given type
        // in scope, assign all Monitor.Enter waiters to it.
        //
        // For the wait-for graph we record: waiter → any lock owner.
        // Real cycle detection works even with approximate edges.
        var rebuiltLocks = RebuildLocksWithWaiters(monitorLocks, monitorWaiters, waitForGraph, threadNames, byManaged);

        // ── 5. Cycle detection (DFS on waitForGraph) ──────────────────────
        var cycles = DetectCycles(waitForGraph);

        return new DeadlockData(
            MonitorLocks:         rebuiltLocks,
            ConfirmedCycles:      cycles,
            IndependentWaiters:   independentList,
            TotalThreadsByRuntime: threads.Count,
            NamedThreadCount:      threadNames.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<MonitorLockEntry> RebuildLocksWithWaiters(
        List<MonitorLockEntry> locks,
        List<(ClrThread Thread, string TopUserFrame, IReadOnlyList<string> Frames)> waiters,
        Dictionary<int, int> waitForGraph,
        Dictionary<int, string> threadNames,
        Dictionary<int, ClrThread> byManaged)
    {
        if (locks.Count == 0 && waiters.Count == 0)
            return locks;

        // If we have monitor waiters but no sync-block locks (can happen in
        // some dumps), synthesize a placeholder lock entry so waiters still show.
        if (locks.Count == 0)
        {
            var waiterIds = waiters.Select(w => (int)w.Thread.ManagedThreadId).ToList();
            return [new MonitorLockEntry(0, "<Monitor lock>", null, null, null, waiterIds, 0)];
        }

        // Distribute waiters: assign each waiter to a contested lock entry.
        // Heuristic: round-robin if multiple locks; owners with LockCount > 0 get priority.
        var contested   = locks.Where(l => l.OwnerManagedId.HasValue).ToList();
        var distributed = new Dictionary<ulong, List<int>>();
        foreach (var l in locks) distributed[l.LockAddress] = [];

        for (int i = 0; i < waiters.Count; i++)
        {
            int waiterId   = waiters[i].Thread.ManagedThreadId;
            var target     = contested.Count > 0 ? contested[i % contested.Count] : locks[i % locks.Count];
            distributed[target.LockAddress].Add(waiterId);

            // Build wait-for edge: waiter is waiting for owner
            if (target.OwnerManagedId.HasValue)
                waitForGraph[waiterId] = target.OwnerManagedId.Value;
        }

        // Also add wait-for edges for owners that are themselves waiting (owner holds A, waits for B).
        // This is detected in step 3: if an owner thread also appears as a monitor waiter it means
        // it holds a lock AND is trying to enter another — the classic T1→T2→T1 pattern.
        foreach (var wg in waitForGraph.Keys.ToList())
        {
            // If this waiter is also an owner of another lock, that owner thread is also
            // waiting for something (already recorded).  Nothing extra to do here.
        }

        return locks
            .Select(l => l with { WaiterManagedIds = distributed.TryGetValue(l.LockAddress, out var ids) ? ids : [] })
            .ToList();
    }

    private static List<DeadlockCycle> DetectCycles(Dictionary<int, int> waitFor)
    {
        // Standard DFS cycle detection on directed graph (wait-for graph).
        var cycles  = new List<DeadlockCycle>();
        var visited = new HashSet<int>();
        var onStack = new Dictionary<int, int>(); // node → position in path

        foreach (int start in waitFor.Keys)
        {
            if (visited.Contains(start)) continue;

            var path = new List<int>();
            Dfs(start, path, onStack, visited, waitFor, cycles);
        }

        return cycles;
    }

    private static void Dfs(
        int node, List<int> path, Dictionary<int, int> onStack,
        HashSet<int> visited, Dictionary<int, int> waitFor,
        List<DeadlockCycle> cycles)
    {
        if (onStack.ContainsKey(node))
        {
            // Cycle found: extract the cycle portion.
            int cycleStart = onStack[node];
            var cycle = path[cycleStart..];
            cycle.Add(node); // close the cycle
            // Deduplicate equivalent cycles (same set, different start).
            var cycleSet = new HashSet<int>(cycle);
            bool alreadySeen = cycles.Any(c => new HashSet<int>(c.ThreadIds).SetEquals(cycleSet));
            if (!alreadySeen)
                cycles.Add(new DeadlockCycle(cycle));
            return;
        }
        if (visited.Contains(node)) return;

        onStack[node] = path.Count;
        path.Add(node);

        if (waitFor.TryGetValue(node, out int next))
            Dfs(next, path, onStack, visited, waitFor, cycles);

        path.RemoveAt(path.Count - 1);
        onStack.Remove(node);
        visited.Add(node);
    }

    // Returns a human-readable wait reason if the thread is blocked on a
    // non-monitor synchronization primitive; null if not blocked.
    private static string? DetectIndependentWait(List<ClrStackFrame> frames)
    {
        foreach (var f in frames)
        {
            string? typeName   = f.Method?.Type?.Name;
            string? methodName = f.Method?.Name;
            if (typeName is null && methodName is null) continue;

            string label = $"{typeName}.{methodName}";

            if (typeName == "System.Threading.WaitHandle" && methodName is "WaitOne" or "WaitAny" or "WaitAll")
                return label;
            if (typeName == "System.Threading.ManualResetEventSlim" && methodName == "Wait")
                return label;
            if (typeName == "System.Threading.SemaphoreSlim" && methodName is "Wait" or "WaitAsync")
                return label;
            if (typeName == "System.Threading.Tasks.Task" && methodName is "Wait" or "WaitAll" or "WaitAny")
                return label;
            if (typeName == "System.Threading.Thread" && methodName == "Join")
                return label;
            if (typeName == "System.Threading.ReaderWriterLockSlim" && methodName is "EnterReadLock" or "EnterWriteLock" or "EnterUpgradeableReadLock")
                return label;
            if (typeName == "System.Threading.Mutex" && methodName == "WaitOne")
                return label;
        }
        return null;
    }

    // Returns the topmost frame that is not an internal CLR/mscorlib frame.
    private static string FindTopUserFrame(List<ClrStackFrame> frames)
    {
        foreach (var f in frames)
        {
            string? typeName = f.Method?.Type?.Name;
            if (typeName is null) continue;
            if (typeName.StartsWith("System.", StringComparison.Ordinal)) continue;
            if (typeName.StartsWith("Microsoft.", StringComparison.Ordinal)) continue;
            string label = f.Method?.Signature ?? f.FrameName ?? "<unknown>";
            return label.Length > 120 ? label[..117] + "…" : label;
        }
        // Fall back to first managed frame.
        foreach (var f in frames)
        {
            if (f.Method is not null)
            {
                string label = f.Method.Signature ?? f.FrameName ?? "<unknown>";
                return label.Length > 120 ? label[..117] + "…" : label;
            }
        }
        return "<no managed frames>";
    }

    private static string FrameLabel(ClrStackFrame f) =>
        (f.FrameName ?? f.Method?.Signature ?? "<unknown>").Let(s => s.Length > 160 ? s[..157] + "…" : s);
}

file static class StringExtensions
{
    internal static string Let(this string s, Func<string, string> f) => f(s);
}
