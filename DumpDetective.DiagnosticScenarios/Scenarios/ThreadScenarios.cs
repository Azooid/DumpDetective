namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: thread-analysis, thread-pool, thread-pool-starvation, deadlock-detection
internal static class ThreadScenarios
{
    // ── thread-analysis ───────────────────────────────────────────────────────
    // 20 named background threads, each blocked on a ManualResetEventSlim.
    // Taking a dump while they're blocked shows them on the thread list with
    // recognisable names and a wait-handle stack frame.
    private static readonly ManualResetEventSlim _threadGate = new(false);
    private static readonly List<Thread> _namedThreads = [];
    private static int _threadCount;

    public static IResult TriggerThreadAnalysis()
    {
        if (_threadCount > 0)
            return Results.Ok(new { message = $"{_threadCount} named threads already running." });

        const int count = 20;
        for (int i = 0; i < count; i++)
        {
            int id = i;
            var t = new Thread(() =>
            {
                _threadGate.Wait(); // blocks here until Reset() is called
            })
            {
                Name = $"DiagWorker-{id:D2}",
                IsBackground = true,
            };
            _namedThreads.Add(t);
            t.Start();
        }
        _threadCount = count;
        return Results.Ok(new { message = $"{count} named threads blocked on a wait handle.", command = "DumpDetective thread-analysis <dump.dmp>" });
    }

    public static string ThreadStatus => $"thread-analysis: {_threadCount} named threads active";

    // ── thread-pool ───────────────────────────────────────────────────────────
    // Queue 80 work items that each block on a gate so Reset() can free them
    // instantly instead of waiting up to 5 minutes for Thread.Sleep to expire.
    private static int _poolItemsQueued;
    private static ManualResetEventSlim _poolGate = new(false);

    public static IResult TriggerThreadPool()
    {
        if (_poolItemsQueued > 0)
            return Results.Ok(new { message = $"{_poolItemsQueued} pool items already queued." });

        const int count = 80;
        var gate = _poolGate; // capture — Reset() will replace the field
        for (int i = 0; i < count; i++)
            ThreadPool.QueueUserWorkItem(_ => gate.Wait(TimeSpan.FromMinutes(10)));

        _poolItemsQueued = count;

        // Block this handler thread on the same gate.
        // The HTTP client will time out — that IS the signal the pool is saturating.
        // Reset() signals the gate, which unblocks all work items AND this handler,
        // allowing Kestrel to close the connection cleanly.
        gate.Wait(TimeSpan.FromMinutes(10));
        _poolItemsQueued = 0;
        return Results.Ok(new { message = $"{count} thread-pool work items completed after reset." });
    }

    public static string PoolStatus => $"thread-pool: {_poolItemsQueued} items queued";

    // ── thread-pool-starvation ────────────────────────────────────────────────
    // Queues 60 work items that each block a pool thread via sync-over-async.
    // Uses a CancellationToken so Reset() can unblock them immediately.
    private static int _starvationItemsQueued;
    private static CancellationTokenSource _starvationCts = new();

    public static IResult TriggerThreadPoolStarvation()
    {
        if (_starvationItemsQueued > 0)
            return Results.Ok(new { message = $"{_starvationItemsQueued} starvation items already running." });

        const int count = 60;
        var token = _starvationCts.Token; // capture — Reset() will replace the CTS
        for (int i = 0; i < count; i++)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Task.Delay(TimeSpan.FromMinutes(10), token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            });
        }
        _starvationItemsQueued = count;

        // Block this handler thread on the same token.
        // Reset() cancels the CTS, which unblocks all work items AND this handler.
        try { Task.Delay(TimeSpan.FromMinutes(10), token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _starvationItemsQueued = 0;
        return Results.Ok(new { message = $"{count} starvation items completed after reset." });
    }

    public static string StarvationStatus => $"thread-pool-starvation: {_starvationItemsQueued} blocking items";

    // ── deadlock-detection ────────────────────────────────────────────────────
    // Thread A acquires _lockA → waits for _lockB.
    // Thread B acquires _lockB → waits for _lockA.
    // The handler intentionally blocks on _deadlockGate so the dump is taken
    // while the deadlock is live.  Reset() unblocks the handler and replaces
    // the permanently-held lock objects so the scenario can be re-triggered.
    private static object _lockA = new();
    private static object _lockB = new();
    private static ManualResetEventSlim _deadlockGate = new(false);
    private static bool _deadlocked;

    public static IResult TriggerDeadlock()
    {
        if (_deadlocked)
            return Results.Ok(new { message = "Deadlock already active." });

        // Capture current instances — Reset() will replace the fields.
        var lockA = _lockA;
        var lockB = _lockB;
        var gate  = _deadlockGate;

        using var syncA = new SemaphoreSlim(0, 1);
        using var syncB = new SemaphoreSlim(0, 1);

        var threadA = new Thread(() =>
        {
            lock (lockA)
            {
                syncA.Release();
                syncB.Wait();
                lock (lockB) { /* deadlocked — never reached */ }
            }
        }) { Name = "Deadlock-Thread-A", IsBackground = true };

        var threadB = new Thread(() =>
        {
            lock (lockB)
            {
                syncB.Release();
                syncA.Wait();
                lock (lockA) { /* deadlocked — never reached */ }
            }
        }) { Name = "Deadlock-Thread-B", IsBackground = true };

        threadA.Start();
        threadB.Start();

        // Wait for both threads to hold their first lock (takes only a few ms).
        syncA.Wait();
        syncB.Wait();
        // Both threads are now waiting for each other — deadlock is live.
        _deadlocked = true;

        // Block this handler on the gate so the request hangs intentionally.
        // Reset() signals the gate, allowing Kestrel to close the connection.
        gate.Wait(TimeSpan.FromMinutes(10));
        _deadlocked = false;
        return Results.Ok(new { message = "Deadlock scenario completed after reset." });
    }

    public static string DeadlockStatus => $"deadlock-detection: {(_deadlocked ? "ACTIVE" : "not triggered")}";

    public static void Reset()
    {
        // Unblock named threads
        _threadGate.Set();
        _namedThreads.Clear();
        _threadCount = 0;

        // Unblock pool-gate threads immediately; fresh gate for next trigger
        _poolGate.Set();
        _poolGate = new ManualResetEventSlim(false);
        _poolItemsQueued = 0;

        // Cancel starvation tasks immediately; fresh CTS for next trigger
        _starvationCts.Cancel();
        _starvationCts.Dispose();
        _starvationCts = new CancellationTokenSource();
        _starvationItemsQueued = 0;

        // Unblock deadlock handler; replace permanently-held locks for next trigger
        var oldGate = _deadlockGate;
        _deadlockGate = new ManualResetEventSlim(false);
        _lockA = new object();
        _lockB = new object();
        oldGate.Set();
    }
}
