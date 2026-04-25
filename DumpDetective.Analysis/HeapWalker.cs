using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDetective.Analysis;

/// <summary>
/// Performs a single <c>heap.EnumerateObjects()</c> pass and fans output
/// to every registered <see cref="IHeapObjectConsumer"/>.
/// Adding a new metric requires adding a new consumer — not editing this class.
/// </summary>
public static class HeapWalker
{
    /// <summary>
    /// Walks all live, non-free objects on <paramref name="heap"/> exactly once,
    /// dispatching each to every consumer in <paramref name="consumers"/>.
    /// <summary>
    /// Walks all live objects on <paramref name="heap"/> exactly once,
    /// dispatching each to every consumer in <paramref name="consumers"/>.
    /// <see cref="IHeapObjectConsumer.OnWalkComplete"/> is called on every
    /// consumer after the walk, even if the walk throws.
    /// Free objects are counted but not dispatched to consumers.
    /// </summary>
    /// <returns>Total size in bytes of all free objects (used to compute fragmentation).</returns>
    public static long Walk(
        ClrHeap                       heap,
        IReadOnlyList<IHeapObjectConsumer> consumers,
        Action<string>?               progress = null)
    {
        const int MaxParallelSegments = 8;

        long freeBytes      = 0;
        long processedCount = 0;
        var  totalWatch     = progress is not null ? Stopwatch.StartNew() : null;
        var  rateWatch      = progress is not null ? Stopwatch.StartNew() : null;
        long lastCount      = 0;

        // Shared meta cache — lock-free; BuildMeta is idempotent so two threads
        // racing on the same MT both compute but only one entry is stored.
        var metaCache = new ConcurrentDictionary<ulong, HeapTypeMeta>(
            concurrencyLevel: MaxParallelSegments, capacity: 8192);

        var segments = heap.Segments.ToArray();

        // Group segments into exactly MaxParallelSegments buckets (round-robin over segments
        // sorted largest-first for load balance). This caps clone count at 8 regardless of how
        // many segments exist — 63 segments would otherwise create 63×N consumer clones
        // simultaneously, causing multi-GB memory spikes on large heaps.
        int bucketCount = Math.Min(MaxParallelSegments, segments.Length);
        var buckets     = new List<ClrSegment>[bucketCount];
        for (int b = 0; b < bucketCount; b++) buckets[b] = [];

        var sorted = segments.OrderByDescending(s => s.ObjectRange.Length).ToArray();
        for (int i = 0; i < sorted.Length; i++)
            buckets[i % bucketCount].Add(sorted[i]);

        // One clone-set per bucket — 8 clones total instead of 63.
        // Thread-safe consumers (IsThreadSafe == true) are shared directly across all
        // bucket workers — no cloning, no merging. This avoids multi-GB duplicate
        // allocations for large consumers like InboundRefConsumer and ReferrerConsumer.
        var bucketClones = new IHeapObjectConsumer[bucketCount][];
        for (int b = 0; b < bucketCount; b++)
        {
            var clones = new IHeapObjectConsumer[consumers.Count];
            for (int c = 0; c < consumers.Count; c++)
                clones[c] = consumers[c].IsThreadSafe ? consumers[c] : consumers[c].CreateClone();
            bucketClones[b] = clones;
        }

        try
        {
            Parallel.ForEach(
                Enumerable.Range(0, bucketCount),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSegments },
                bucketIdx =>
                {
                    var clones = bucketClones[bucketIdx];
                    foreach (var seg in buckets[bucketIdx])
                    foreach (var obj in seg.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type is null) continue;

                        if (obj.Type.IsFree)
                        {
                            Interlocked.Add(ref freeBytes, (long)obj.Size);
                            continue;
                        }

                        ulong mt   = obj.Type.MethodTable;
                        var   meta = metaCache.GetOrAdd(mt,
                            static (_, t) => BuildMeta(t, includeDelegateFields: true),
                            obj.Type);

                        for (int i = 0; i < clones.Length; i++)
                            clones[i].Consume(in obj, meta, heap);

                        if (progress is not null)
                        {
                            long cur = Interlocked.Increment(ref processedCount);
                            if ((cur & 0x3FF) == 0 && rateWatch!.ElapsedMilliseconds >= 200)
                            {
                                double elapsed  = totalWatch!.Elapsed.TotalSeconds;
                                double interval = rateWatch.Elapsed.TotalSeconds;
                                long   snap     = Interlocked.Read(ref processedCount);
                                long   delta    = snap - Interlocked.Exchange(ref lastCount, snap);
                                long   rate     = interval > 0 ? (long)(delta / interval) : 0;
                                rateWatch.Restart();
                                progress($"Walking heap objects — {snap:N0} objs  •  {elapsed:F1}s  •  ~{rate:N0}/s");
                            }
                        }
                    }
                });
        }
        finally
        {
            // ── Parallel merge — 8 bucket-clones, single progress thread ─────────
            long mergeStepsDone  = 0;
            long mergeStepsTotal = (long)consumers.Count * bucketCount;
            long objCount        = Interlocked.Read(ref processedCount);
            var  mergeDone       = new ManualResetEventSlim(false);

            Thread? progressThread = null;
            if (progress is not null)
            {
                progressThread = new Thread(() =>
                {
                    while (!mergeDone.Wait(200))
                    {
                        double elapsed = totalWatch!.Elapsed.TotalSeconds;
                        long   steps   = Interlocked.Read(ref mergeStepsDone);
                        progress($"Walking heap objects — {objCount:N0} objs  •  {elapsed:F1}s  •  merging {steps}/{mergeStepsTotal}...");
                    }
                }) { IsBackground = true, Name = "HeapWalker.MergeProgress" };
                progressThread.Start();
            }

            try
            {
                Parallel.For(0, consumers.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSegments },
                    c =>
                    {
                        for (int b = 0; b < bucketCount; b++)
                        {
                            if (!consumers[c].IsThreadSafe)
                            {
                                consumers[c].MergeFrom(bucketClones[b][c]);
                            }
                            Interlocked.Increment(ref mergeStepsDone);
                        }
                    });
            }
            finally
            {
                mergeDone.Set();
                progressThread?.Join();
            }

            // ── Finalise: OnWalkComplete + GC — keep spinner alive throughout ──
            var finaliseDone = new ManualResetEventSlim(false);
            Thread? finaliseThread = null;
            if (progress is not null)
            {
                finaliseThread = new Thread(() =>
                {
                    while (!finaliseDone.Wait(200))
                    {
                        double elapsed = totalWatch!.Elapsed.TotalSeconds;
                        progress($"Walking heap objects — {objCount:N0} objs  •  {elapsed:F1}s  •  finalising...");
                    }
                }) { IsBackground = true, Name = "HeapWalker.FinaliseProgress" };
                finaliseThread.Start();
            }

            try
            {
                for (int i = 0; i < consumers.Count; i++)
                    consumers[i].OnWalkComplete();

                for (int b = 0; b < bucketCount; b++)
                    for (int c = 0; c < bucketClones[b].Length; c++)
                        bucketClones[b][c] = null!;

                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            }
            finally
            {
                finaliseDone.Set();
                finaliseThread?.Join();
            }
        }

        // Final [SCAN] message — clears the live spinner and prints total result
        if (progress is not null && totalWatch is not null)
            progress($"[SCAN]Heap walk|{processedCount}|{(long)totalWatch.Elapsed.TotalMilliseconds}");

        return freeBytes;
    }

    // ── Shared HeapTypeMeta builder ───────────────────────────────────────────

    internal static readonly HashSet<string> TimerTypeSet = new(
    [
        "System.Threading.TimerQueueTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Windows.Forms.Timer",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> HttpTypeSet = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpRequestMessage",
        "System.Net.Http.HttpResponseMessage",
        "System.Net.HttpWebRequest",
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpClientHandler",
        "System.Net.Http.SocketsHttpHandler",
    };

    private static readonly string[] WorkItemTypes =
    [
        "System.Threading.QueueUserWorkItemCallback",
        "System.Threading.QueueUserWorkItemCallbackDefaultContext",
    ];

    internal static readonly string[] ConnectionPrefixes =
    [
        "System.Data.SqlClient.SqlConnection",
        "Microsoft.Data.SqlClient.SqlConnection",
        "MySql.Data.MySqlClient.MySqlConnection",
        "Oracle.ManagedDataAccess.Client.OracleConnection",
        "Npgsql.NpgsqlConnection",
        "System.Data.OleDb.OleDbConnection",
        "System.Data.Odbc.OdbcConnection",
        "Microsoft.EntityFrameworkCore.DbContext",
        "System.Data.Entity.DbContext",
    ];

    internal static HeapTypeMeta BuildMeta(ClrType type, bool includeDelegateFields)
    {
        string typeName = type.Name ?? "<unknown>";

        bool isConnection = false;
        for (int i = 0; i < ConnectionPrefixes.Length; i++)
        {
            if (typeName.StartsWith(ConnectionPrefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                isConnection = true;
                break;
            }
        }

        string? asyncMethod =
            typeName.Contains(">d__", StringComparison.Ordinal) ||
            typeName.Contains(">D__", StringComparison.Ordinal)
            ? ExtractAsyncMethod(typeName)
            : null;

        DelegateFieldMeta[] delegateFields = [];
        if (includeDelegateFields && !DumpHelpers.IsSystemType(typeName))
        {
            var tmp = new List<DelegateFieldMeta>();
            foreach (var f in type.Fields)
            {
                if (!f.IsObjectReference || f.Type is null || !IsDelegate(f.Type))
                    continue;
                var ft = f.Type.Name ?? string.Empty;
                if (ft.StartsWith("System.Action",           StringComparison.Ordinal) ||
                    ft.StartsWith("System.Func",             StringComparison.Ordinal) ||
                    ft.StartsWith("System.Threading.Thread", StringComparison.Ordinal))
                    continue;
                tmp.Add(new DelegateFieldMeta(f, f.Name ?? "<?>"));
            }
            if (tmp.Count > 0)
                delegateFields = [.. tmp];
        }

        return new HeapTypeMeta
        {
            Name         = typeName,
            MT           = type.MethodTable,
            IsException  = DumpHelpers.IsExceptionType(type),
            AsyncMethod  = asyncMethod,
            IsTimer      = TimerTypeSet.Contains(typeName),
            IsWcf        = typeName.StartsWith("System.ServiceModel.", StringComparison.OrdinalIgnoreCase),
            IsConnection = isConnection,
            IsThread     = typeName == "System.Threading.Thread",
            IsTask       = typeName == "System.Threading.Tasks.Task" ||
                           typeName.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
                           typeName.StartsWith("System.Threading.Tasks.Task`", StringComparison.Ordinal),
            IsWorkItem   = WorkItemTypes.AsSpan().IndexOf(typeName) >= 0 ||
                           typeName.Contains("WorkItem",    StringComparison.OrdinalIgnoreCase) ||
                           typeName.Contains("WorkRequest", StringComparison.OrdinalIgnoreCase),
            IsHttp       = HttpTypeSet.Contains(typeName),
            IsCwt        = typeName.StartsWith("System.Runtime.CompilerServices.ConditionalWeakTable",
                               StringComparison.Ordinal),
            DelegateFields = delegateFields,
        };
    }

    private static string? ExtractAsyncMethod(string typeName)
    {
        // Match the old DumpCollector algorithm exactly:
        // LastIndexOf('<') first, then search forward for ">d__" from that position.
        // When the type has a trailing generic suffix (e.g., +<Bar>d__N<T>), LastIndexOf finds the
        // trailing '<T>' rather than the method '<Bar>', so gt is not found — fall back to the full
        // type name, which exactly matches old behavior (separate row per specialisation).
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", lt < 0 ? 0 : lt, StringComparison.Ordinal);
        if (lt < 0 || gt < 0) return typeName;   // fallback: use full name as key (old behavior)

        string declaring = typeName[..lt].TrimEnd('+');
        string method    = typeName[(lt + 1)..gt];
        return $"{declaring} .{method}";
    }

    private static bool IsDelegate(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "System.MulticastDelegate" or "System.Delegate")
                return true;
        return false;
    }

    /// <summary>
    /// Counts the number of live subscribers on a delegate object.
    /// Shared by <see cref="Consumers.LightweightStatsConsumer"/> and
    /// <see cref="Analyzers.EventAnalysisAnalyzer"/>.
    /// </summary>
    internal static int CountSubscribers(ClrObject del)
    {
        try
        {
            var invList = del.ReadObjectField("_invocationList");
            if (!invList.IsNull && invList.IsValid && invList.Type?.IsArray == true)
            {
                int count = 0;
                var arr = invList.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var item = arr.GetObjectValue(i);
                    if (!item.IsValid || item.IsNull) continue;
                    if (!item.ReadObjectField("_target").IsNull) count++;
                }
                return count;
            }
            return del.ReadObjectField("_target").IsNull ? 0 : 1;
        }
        catch { return 0; }
    }
}
