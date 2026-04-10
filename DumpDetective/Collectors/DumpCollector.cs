using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Collectors;

public static class DumpCollector
{
    private static readonly HashSet<string> TimerTypeSet = new(
    [
        "System.Threading.TimerQueueTimer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Windows.Forms.Timer",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ConnectionPrefixes =
    [
        "System.Data.SqlClient.SqlConnection",
        "Microsoft.Data.SqlClient.SqlConnection",
        "System.Data.OleDb.OleDbConnection",
        "System.Data.Odbc.OdbcConnection",
        "Oracle.ManagedDataAccess.Client.OracleConnection",
        "Npgsql.NpgsqlConnection",
        "MySql.Data.MySqlClient.MySqlConnection",
        "Microsoft.EntityFrameworkCore.DbContext",
        "System.Data.Entity.DbContext",
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Full collection (string dups + event leaks) using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectFull(DumpContext ctx)        => CollectFromContext(ctx, full: true);

    /// <summary>Lightweight collection using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectLightweight(DumpContext ctx) => CollectFromContext(ctx, full: false);

    /// <summary>Full collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectFull(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: true, progress);

    /// <summary>Lightweight collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectLightweight(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: false, progress);

    // ── Private collect paths ─────────────────────────────────────────────────

    private static DumpSnapshot CollectFromContext(DumpContext ctx, bool full)
    {

        var snapshot = CreateSnapshot(ctx.DumpPath, ctx.FileTime, full);
        snapshot.ClrVersion = ctx.ClrVersion;
        return FinalizeSnapshot(ctx.Runtime, snapshot, full, progress: null);
    }

    private static DumpSnapshot Collect(string dumpPath, bool full, Action<string>? progress = null)
    {
        var snapshot = CreateSnapshot(
            dumpPath,
            File.Exists(dumpPath) ? File.GetLastWriteTime(dumpPath) : DateTime.UtcNow,
            full);

        var (runtime, dataTarget) = DumpHelpers.OpenDump(dumpPath);
        using var _dt = dataTarget;
        using var _rt = runtime;

        if (runtime is null) return snapshot;

        snapshot.ClrVersion = runtime.ClrInfo?.Version.ToString();
        return FinalizeSnapshot(runtime, snapshot, full, progress);
    }

    private static DumpSnapshot CreateSnapshot(string dumpPath, DateTime fileTime, bool full)
        => new()
        {
            DumpPath          = dumpPath,
            DumpFileSizeBytes = File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0,
            FileTime          = fileTime,
            IsFullMode        = full,
        };

    private static DumpSnapshot FinalizeSnapshot(ClrRuntime runtime, DumpSnapshot snapshot, bool full, Action<string>? progress)
    {
        CollectAll(runtime, snapshot, full, progress);
        GenerateFindings(snapshot);
        return snapshot;
    }

    /// <summary>Core collection logic — works with any ClrRuntime instance.</summary>
    private static void CollectAll(ClrRuntime runtime, DumpSnapshot snapshot, bool full, Action<string>? progress = null)
    {
        progress?.Invoke("Reading threads...");
        CollectThreads(runtime, snapshot);
        CollectThreadPool(runtime, snapshot);
        progress?.Invoke("Reading GC handles...");
        CollectHandles(runtime, snapshot);
        progress?.Invoke("Reading modules...");
        CollectModules(runtime, snapshot);

        var heap = runtime.Heap;
        if (heap.CanWalkHeap)
        {
            progress?.Invoke("Reading heap segments...");
            CollectSegmentLayout(heap, snapshot);
            progress?.Invoke("Walking heap objects...");
            CollectHeapObjects(heap, snapshot, full, progress);
            progress?.Invoke("Reading finalizer queue...");
            CollectFinalizerQueue(heap, snapshot);
        }
    }

    // ── Threads ───────────────────────────────────────────────────────────────

    private static void CollectThreads(ClrRuntime runtime, DumpSnapshot s)
    {
        var threads = runtime.Threads.ToList();
        s.ThreadCount          = threads.Count;
        s.AliveThreadCount     = threads.Count(t => t.IsAlive);
        s.ExceptionThreadCount = threads.Count(t => t.CurrentException is not null);
        s.BlockedThreadCount   = threads.Count(t =>
            t.EnumerateStackTrace().Take(5).Any(f =>
            {
                var name = f.Method?.Name ?? string.Empty;
                return name is "WaitOne" or "Wait" or "Enter" or "TryEnter" or "Join"
                    || name.Contains("Wait", StringComparison.OrdinalIgnoreCase);
            }));
    }

    // ── Thread pool ───────────────────────────────────────────────────────────

    private static void CollectThreadPool(ClrRuntime runtime, DumpSnapshot s)
    {
        var tp = runtime.ThreadPool;
        if (tp is null) return;
        s.TpMinWorkers    = tp.MinThreads;
        s.TpMaxWorkers    = tp.MaxThreads;
        s.TpActiveWorkers = tp.ActiveWorkerThreads;
        s.TpIdleWorkers   = tp.IdleWorkerThreads;
    }

    // ── GC handles ────────────────────────────────────────────────────────────

    private static void CollectHandles(ClrRuntime runtime, DumpSnapshot s)
    {
        var rootedByKey = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);

        foreach (var h in runtime.EnumerateHandles())
        {
            s.TotalHandleCount++;
            if (h.IsPinned)  s.PinnedHandleCount++;
            if (h.IsStrong)  s.StrongHandleCount++;
            if (h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
                s.WeakHandleCount++;

            if (h.IsStrong)
            {
                try
                {
                    var obj = h.Object;
                    if (obj == 0) continue;
                    var heapObj = runtime.Heap.GetObject(obj);
                    if (!heapObj.IsValid) continue;
                    var typeName = heapObj.Type?.Name ?? "<unknown>";
                    var key      = $"{h.HandleKind}|{typeName}";
                    long size    = (long)heapObj.Size;
                    if (rootedByKey.TryGetValue(key, out var e))
                        rootedByKey[key] = (e.Count + 1, e.Size + size);
                    else
                        rootedByKey[key] = (1, size);
                }
                catch { }
            }
        }

        s.TopRootedTypes = rootedByKey
            .OrderByDescending(kv => kv.Value.Count)
            .Take(15)
            .Select(kv =>
            {
                var sep      = kv.Key.IndexOf('|');
                var kind     = sep >= 0 ? kv.Key[..sep]   : kv.Key;
                var typeName = sep >= 0 ? kv.Key[(sep+1)..] : string.Empty;
                return new RootedHandleStat(kind, typeName, kv.Value.Count, kv.Value.Size);
            })
            .ToList();
    }

    // ── Modules ───────────────────────────────────────────────────────────────

    private static void CollectModules(ClrRuntime runtime, DumpSnapshot s)
    {
        foreach (var m in runtime.EnumerateModules())
        {
            s.ModuleCount++;
            var path = m.Name ?? m.AssemblyName ?? string.Empty;
            if (!IsSystemAssemblyPath(path)) s.AppModuleCount++;
        }
    }

    // ── Segment layout (gen totals + fragmentation estimate) ─────────────────

    private static void CollectSegmentLayout(ClrHeap heap, DumpSnapshot s)
    {
        foreach (var seg in heap.Segments)
        {
            long segCommitted = (long)seg.CommittedMemory.Length;

            switch (seg.Kind)
            {
                case GCSegmentKind.Generation0: s.Gen0Bytes   += segCommitted; break;
                case GCSegmentKind.Generation1: s.Gen1Bytes   += segCommitted; break;
                case GCSegmentKind.Generation2: s.Gen2Bytes   += segCommitted; break;
                case GCSegmentKind.Ephemeral:
                    s.Gen0Bytes += (long)seg.Generation0.Length;
                    s.Gen1Bytes += (long)seg.Generation1.Length;
                    s.Gen2Bytes += (long)seg.Generation2.Length;
                    break;
                case GCSegmentKind.Large:  s.LohBytes    += segCommitted; break;
                case GCSegmentKind.Pinned: s.PohBytes    += segCommitted; break;
                case GCSegmentKind.Frozen: s.FrozenBytes += segCommitted; break;
            }
        }

        s.TotalHeapBytes = s.Gen0Bytes + s.Gen1Bytes + s.Gen2Bytes
                         + s.LohBytes + s.PohBytes + s.FrozenBytes;
        // FragmentationPct is set in CollectHeapObjects to avoid a second full heap walk
    }

    // ── Main heap object walk ─────────────────────────────────────────────────

    private static void CollectHeapObjects(ClrHeap heap, DumpSnapshot s, bool full, Action<string>? progress = null)
    {
        long committed = 0;
        foreach (var seg in heap.Segments)
            committed += (long)seg.CommittedMemory.Length;

        long freeBytes    = 0;
        long lohLiveBytes = 0;  // sum of live object sizes ≥85 KB (actual LOH residents)

        var typeStatsByMt = new Dictionary<ulong, TypeAgg>(8192);
        var exCounts      = new Dictionary<string, int>(128, StringComparer.Ordinal);
        var asyncCounts   = new Dictionary<string, int>(512, StringComparer.Ordinal);

        var collectFullData = full;
        var stringValues = collectFullData ? new Dictionary<string, (int Count, long Size)>(65536, StringComparer.Ordinal) : null;
        var eventLeakTotals = collectFullData ? new Dictionary<(string Publisher, string Field), int>(4096) : null;

        var typeMetaCache = new Dictionary<ulong, HeapTypeMeta>(8192);

        int timerCount = 0, wcfCount = 0, wcfFaulted = 0, connCount = 0;
        long processedCount = 0;
        long totalObjects = 0;
        long largestFreeBlock = 0;
        var watch = new Stopwatch();
        watch.Start();
        foreach (var obj in heap.EnumerateObjects())
        {
            processedCount++;
            if(watch is not null && progress is not null && watch.Elapsed.TotalSeconds >= 1)
            {
                progress!($"Walking heap objects — {processedCount:N0} processed");
                watch.Restart();
            }

            if (!obj.IsValid || obj.Type is null)
                continue;

            var type = obj.Type;
            ulong mt = type.MethodTable;
            if (!typeMetaCache.TryGetValue(mt, out var meta))
            {
                var computedTypeName = type.Name ?? string.Empty;
                meta = BuildHeapTypeMeta(type, computedTypeName, collectFullData);
                typeMetaCache[mt] = meta;
            }
            var typeName = meta.Name;

            long size = (long)obj.Size;

            if (type.IsFree)
            {
                freeBytes += size;

                continue;
            }

            totalObjects++;

            ref TypeAgg typeStat = ref CollectionsMarshal.GetValueRefOrAddDefault(typeStatsByMt, mt, out bool typeExists);
            if (!typeExists)
            {
                typeStat.Name = typeName;
                typeStat.Count = 1;
                typeStat.Size = size;
            }
            else
            {
                typeStat.Count++;
                typeStat.Size += size;
            }

            if (size >= 85_000)
            {
                s.LohObjectCount++;
                lohLiveBytes += size;
            }

            if (meta.IsException)
            {
                ref int ec = ref CollectionsMarshal.GetValueRefOrAddDefault(exCounts, typeName, out bool exExists);
                ec = exExists ? ec + 1 : 1;
            }

            var asyncMethod = meta.AsyncMethod;
            if (asyncMethod is not null)
            {
                ref int ac = ref CollectionsMarshal.GetValueRefOrAddDefault(asyncCounts, asyncMethod, out bool asyncExists);
                ac = asyncExists ? ac + 1 : 1;
            }

            if (meta.IsTimer)
                timerCount++;

            if (meta.IsWcf)
            {
                wcfCount++;
                if (TryReadIntField(obj, "_state", "_communicationState") == 5)
                    wcfFaulted++;
            }

            if (meta.IsConnection)
                connCount++;

            if (!collectFullData)
                continue;

            if (stringValues is not null && type.IsString)
            {
                var val = obj.AsString(maxLength: 512) ?? string.Empty;
                ref var sv = ref CollectionsMarshal.GetValueRefOrAddDefault(stringValues, val, out bool svExists);
                sv = svExists ? (sv.Count + 1, sv.Size + size) : (1, size);
                s.StringTotalBytes += size;
            }

            if (eventLeakTotals is not null)
            {
                foreach (var field in meta.DelegateFields)
                {
                    try
                    {
                        var delVal = field.Field.ReadObject(obj.Address, false);
                        if (delVal.IsNull || !delVal.IsValid)
                            continue;

                        int subs = CountSubscribers(delVal);
                        if (subs > 0)
                        {
                            (string Publisher, string Field) key = (typeName, field.Name);
                            ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault<(string Publisher, string Field), int>(eventLeakTotals, key, out bool leakExists);
                            existing = leakExists ? existing + subs : subs;
                        }
                    }
                    catch { }
                }
            }
        }
        s.FragmentationPct = committed > 0 ? freeBytes * 100.0 / committed : 0;
        s.HeapFreeBytes    = freeBytes;
        s.LohLiveBytes     = lohLiveBytes;

        var topTypes = new List<TypeStat>(typeStatsByMt.Count);
        foreach (var kv in typeStatsByMt)
        {
            topTypes.Add(new TypeStat(kv.Value.Name, kv.Value.Count, kv.Value.Size));
        }

        s.TotalObjectCount = totalObjects;
        topTypes.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        if (topTypes.Count > 30)
            topTypes.RemoveRange(30, topTypes.Count - 30);
        s.TopTypes = topTypes;

        var exceptionCounts = new List<(string Type, int Count)>(exCounts.Count);
        foreach (var kv in exCounts)
            exceptionCounts.Add((kv.Key, kv.Value));
        exceptionCounts.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        s.ExceptionCounts = exceptionCounts;

        int asyncBacklogTotal = 0;
        foreach (var kv in asyncCounts)
            asyncBacklogTotal += kv.Value;
        s.AsyncBacklogTotal = asyncBacklogTotal;

        var topAsyncMethods = new List<(string Method, int Count)>(asyncCounts.Count);
        foreach (var kv in asyncCounts)
            topAsyncMethods.Add((kv.Key, kv.Value));
        topAsyncMethods.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        if (topAsyncMethods.Count > 10)
            topAsyncMethods.RemoveRange(10, topAsyncMethods.Count - 10);
        s.TopAsyncMethods = topAsyncMethods;

        s.TimerCount = timerCount;
        s.WcfObjectCount = wcfCount;
        s.WcfFaultedCount = wcfFaulted;
        s.ConnectionCount = connCount;

        if (collectFullData && stringValues is not null)
        {
            s.UniqueStringCount = stringValues.Count;

            int duplicateGroups = 0;
            long wastedBytes = 0;
            var duplicateStats = new List<StringDuplicateStat>();

            foreach (var kv in stringValues)
            {
                if (kv.Value.Count < 2)
                    continue;

                duplicateGroups++;
                long perCopy = kv.Value.Size / kv.Value.Count;
                long wasted = perCopy * (kv.Value.Count - 1);
                wastedBytes += wasted;
                duplicateStats.Add(new StringDuplicateStat(kv.Key, kv.Value.Count, wasted));
            }

            s.StringDuplicateGroups = duplicateGroups;
            s.StringWastedBytes = wastedBytes;
            duplicateStats.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
            if (duplicateStats.Count > 10)
                duplicateStats.RemoveRange(10, duplicateStats.Count - 10);
            s.TopStringDuplicates = duplicateStats;
        }

        if (collectFullData && eventLeakTotals is not null)
        {
            var grouped = new List<EventLeakStat>(eventLeakTotals.Count);
            foreach (var kv in eventLeakTotals)
                grouped.Add(new EventLeakStat(kv.Key.Publisher, kv.Key.Field, kv.Value));
            grouped.Sort(static (a, b) => b.Subscribers.CompareTo(a.Subscribers));

            int eventSubscriberTotal = 0;
            int eventLeakMaxOnField = 0;
            foreach (var item in grouped)
            {
                eventSubscriberTotal += item.Subscribers;
                if (item.Subscribers > eventLeakMaxOnField)
                    eventLeakMaxOnField = item.Subscribers;
            }

            s.EventLeakFieldCount = grouped.Count;
            s.EventSubscriberTotal = eventSubscriberTotal;
            s.EventLeakMaxOnField = eventLeakMaxOnField;
            if (grouped.Count > 10)
                grouped.RemoveRange(10, grouped.Count - 10);
            s.TopEventLeaks = grouped;
        }
    }

    private static HeapTypeMeta BuildHeapTypeMeta(ClrType type, string typeName, bool includeDelegateFields)
    {
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
                if (f.IsObjectReference && f.Type is not null && IsDelegate(f.Type))
                    tmp.Add(new DelegateFieldMeta(f, f.Name ?? "<?>"));
            }

            if (tmp.Count > 0)
                delegateFields = [.. tmp];
        }

        return new HeapTypeMeta(
            typeName,
            DumpHelpers.IsExceptionType(type),
            asyncMethod,
            TimerTypeSet.Contains(typeName),
            typeName.StartsWith("System.ServiceModel.", StringComparison.OrdinalIgnoreCase),
            isConnection,
            delegateFields);
    }

    private sealed class HeapTypeMeta(
        string name,
        bool isException,
        string? asyncMethod,
        bool isTimer,
        bool isWcf,
        bool isConnection,
        DelegateFieldMeta[] delegateFields)
    {
        public string Name { get; } = name;
        public bool IsException { get; } = isException;
        public string? AsyncMethod { get; } = asyncMethod;
        public bool IsTimer { get; } = isTimer;
        public bool IsWcf { get; } = isWcf;
        public bool IsConnection { get; } = isConnection;
        public DelegateFieldMeta[] DelegateFields { get; } = delegateFields;
    }

    private readonly record struct DelegateFieldMeta(ClrInstanceField Field, string Name);

    private struct TypeAgg
    {
        public string Name;
        public long Count;
        public long Size;
    }

    // ── Finalizer queue ───────────────────────────────────────────────────────

    private static void CollectFinalizerQueue(ClrHeap heap, DumpSnapshot s)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var obj in heap.EnumerateFinalizableObjects())
        {
            if (!obj.IsValid) continue;
            var name = obj.Type?.Name ?? "<unknown>";
            counts.TryGetValue(name, out int c);
            counts[name] = c + 1;
        }
        s.FinalizerQueueDepth = counts.Values.Sum();
        s.TopFinalizerTypes   = counts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    // ── Finding generation (scoring) ──────────────────────────────────────────

    private static void GenerateFindings(DumpSnapshot s)
    {
        int score = 100;
        var findings = new List<Finding>();

        void Add(FindingSeverity sev, string cat, string headline, string? detail = null, string? advice = null, int deduct = 0)
        {
            findings.Add(new Finding(sev, cat, headline, detail, advice, deduct));
            score = Math.Max(0, score - deduct);
        }

        // Memory
        if (s.TotalHeapBytes > 2L * 1024 * 1024 * 1024)
            Add(FindingSeverity.Critical, "Memory", $"Heap exceeds 2 GB ({FormatSize(s.TotalHeapBytes)})",
                advice: "Check top memory consumers for leaked collections or cached data.", deduct: 15);
        else if (s.TotalHeapBytes > 800L * 1024 * 1024)
            Add(FindingSeverity.Warning, "Memory", $"Heap is large ({FormatSize(s.TotalHeapBytes)})", deduct: 8);

        if (s.LohBytes > 500L * 1024 * 1024)
            Add(FindingSeverity.Warning, "Memory", $"LOH is {FormatSize(s.LohBytes)}",
                detail: "Large Object Heap cannot be compacted by default.",
                advice: "Pool large byte arrays with ArrayPool<T>.", deduct: 10);

        if (s.FragmentationPct >= 40)
            Add(FindingSeverity.Critical, "Memory", $"Heap fragmentation {s.FragmentationPct:F1}%",
                advice: "Reduce GCHandle.Alloc(Pinned) and use Memory<T> for I/O buffers.", deduct: 10);
        else if (s.FragmentationPct >= 20)
            Add(FindingSeverity.Warning, "Memory", $"Heap fragmentation {s.FragmentationPct:F1}%", deduct: 5);

        // Finalizer queue
        if (s.FinalizerQueueDepth > 500)
            Add(FindingSeverity.Critical, "Memory", $"Finalizer queue has {s.FinalizerQueueDepth:N0} objects",
                advice: "Call Dispose() / use 'using'. Objects on this queue delay GC.", deduct: 15);
        else if (s.FinalizerQueueDepth > 100)
            Add(FindingSeverity.Warning, "Memory", $"Finalizer queue depth: {s.FinalizerQueueDepth:N0}", deduct: 5);

        // Pinned objects
        if (s.PinnedHandleCount > 2000)
            Add(FindingSeverity.Warning, "Memory", $"{s.PinnedHandleCount:N0} pinned GC handles",
                advice: "Replace GCHandle.Alloc(Pinned) with Memory<T> / MemoryPool<T>.", deduct: 5);

        // Event leaks
        if (s.EventLeakMaxOnField > 1000)
            Add(FindingSeverity.Critical, "Leaks", $"Event leak: {s.EventLeakMaxOnField:N0} subscribers on a single field",
                detail: s.TopEventLeaks.FirstOrDefault()?.PublisherType + "." + s.TopEventLeaks.FirstOrDefault()?.FieldName,
                advice: "Unsubscribe event handlers when the subscriber is disposed.", deduct: 20);
        else if (s.EventSubscriberTotal > 500)
            Add(FindingSeverity.Warning, "Leaks", $"Event leaks: {s.EventLeakFieldCount:N0} fields, {s.EventSubscriberTotal:N0} total subscribers", deduct: 10);

        // String waste
        if (s.StringWastedBytes > 100L * 1024 * 1024)
            Add(FindingSeverity.Warning, "Memory", $"String duplication wastes {FormatSize(s.StringWastedBytes)}",
                advice: "Use string interning or shared constants for repeated strings.", deduct: 5);

        // Async backlog
        if (s.AsyncBacklogTotal > 500)
            Add(FindingSeverity.Critical, "Async", $"{s.AsyncBacklogTotal:N0} async continuations suspended",
                detail: s.TopAsyncMethods.FirstOrDefault() is var top && top != default
                    ? $"Top: {top.Method} ({top.Count:N0})" : null,
                advice: "Investigate awaited operations for I/O or lock contention.", deduct: 10);
        else if (s.AsyncBacklogTotal > 100)
            Add(FindingSeverity.Warning, "Async", $"{s.AsyncBacklogTotal:N0} async continuations suspended", deduct: 5);

        // Thread pool
        if (s.TpMaxWorkers > 0 && s.TpActiveWorkers >= s.TpMaxWorkers)
            Add(FindingSeverity.Critical, "Threading", $"Thread pool saturated ({s.TpActiveWorkers}/{s.TpMaxWorkers} workers)",
                advice: "Avoid blocking synchronous calls on thread pool threads.", deduct: 15);
        else if (s.TpActiveWorkers > s.TpMaxWorkers * 0.8 && s.TpMaxWorkers > 0)
            Add(FindingSeverity.Warning, "Threading", $"Thread pool near capacity ({s.TpActiveWorkers}/{s.TpMaxWorkers})", deduct: 5);

        // Blocked threads
        if (s.BlockedThreadCount > 20)
            Add(FindingSeverity.Critical, "Threading", $"{s.BlockedThreadCount:N0} threads appear blocked", deduct: 10);
        else if (s.BlockedThreadCount > 5)
            Add(FindingSeverity.Warning, "Threading", $"{s.BlockedThreadCount:N0} threads appear blocked", deduct: 5);

        // Exceptions on threads
        if (s.ExceptionThreadCount > 5)
            Add(FindingSeverity.Warning, "Exceptions", $"{s.ExceptionThreadCount:N0} threads have active exceptions", deduct: 5);

        // WCF
        if (s.WcfFaultedCount > 0)
            Add(FindingSeverity.Warning, "WCF", $"{s.WcfFaultedCount:N0} faulted WCF channel(s)",
                advice: "Call Abort() on faulted channels and recreate them.", deduct: 10);

        // DB connections
        if (s.ConnectionCount > 50)
            Add(FindingSeverity.Critical, "Connections", $"{s.ConnectionCount:N0} DB connection objects on heap",
                advice: "Wrap SqlConnection in 'using'. Verify connection pooling settings.", deduct: 10);
        else if (s.ConnectionCount > 10)
            Add(FindingSeverity.Warning, "Connections", $"{s.ConnectionCount:N0} DB connection objects on heap", deduct: 5);

        // Timers
        if (s.TimerCount > 500)
            Add(FindingSeverity.Warning, "Leaks", $"{s.TimerCount:N0} timer objects on heap",
                advice: "Dispose System.Timers.Timer instances. Check for timer leak pattern.", deduct: 5);

        if (findings.Count == 0)
            findings.Add(new Finding(FindingSeverity.Info, "Summary", "No significant issues detected."));

        s.Findings    = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ToList();
        s.HealthScore = score;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountSubscribers(ClrObject del)
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
                    var target = item.ReadObjectField("_target");
                    if (!target.IsNull) count++;
                }
                return count;
            }
            var t2 = del.ReadObjectField("_target");
            return t2.IsNull ? 0 : 1;
        }
        catch { return 0; }
    }

    private static bool IsDelegate(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "System.MulticastDelegate" or "System.Delegate")
                return true;
        return false;
    }

    private static int TryReadIntField(ClrObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            try { return obj.ReadField<int>(name); } catch { }
        }
        return -1;
    }

    private static string ExtractAsyncMethod(string typeName)
    {
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", lt < 0 ? 0 : lt, StringComparison.Ordinal);
        if (lt < 0 || gt < 0) return typeName;
        string declaring = typeName[..lt].TrimEnd('+');
        string method    = typeName[(lt + 1)..gt];
        return $"{declaring}.{method}";
    }

    private static bool IsSystemAssemblyPath(string path) =>
        path.Contains("\\dotnet\\shared\\",      StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\windows\\assembly\\",   StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\gac_",                  StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("System.",    StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("mscorlib",   StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) => DumpHelpers.FormatSize(bytes);
}
