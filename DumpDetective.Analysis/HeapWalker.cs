using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using System.Diagnostics;

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
        var typeMetaCache  = new Dictionary<ulong, HeapTypeMeta>(8192);
        long freeBytes     = 0;
        long processedCount = 0;
        var  watch         = progress is not null ? Stopwatch.StartNew() : null;

        try
        {
            foreach (var obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;

                if (obj.Type.IsFree)
                {
                    freeBytes += (long)obj.Size;
                    continue;
                }

                if (progress is not null)
                {
                    processedCount++;
                    if ((processedCount & 0xFFFF) == 0 && watch!.ElapsedMilliseconds >= 1000)
                    {
                        progress($"Walking heap objects — {processedCount:N0} processed");
                        watch.Restart();
                    }
                }

                ulong mt = obj.Type.MethodTable;
                if (!typeMetaCache.TryGetValue(mt, out var meta))
                {
                    meta = BuildMeta(obj.Type, includeDelegateFields: true);
                    typeMetaCache[mt] = meta;
                }

                for (int i = 0; i < consumers.Count; i++)
                    consumers[i].Consume(in obj, meta, heap);
            }
        }
        finally
        {
            for (int i = 0; i < consumers.Count; i++)
                consumers[i].OnWalkComplete();
        }

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
            DelegateFields = delegateFields,
        };
    }

    private static string? ExtractAsyncMethod(string typeName)
    {
        int lt = typeName.LastIndexOf('<');
        int gt = typeName.IndexOf(">d__", StringComparison.Ordinal);
        if (gt < 0) gt = typeName.IndexOf(">D__", StringComparison.Ordinal);
        if (lt < 0 || gt < 0 || gt <= lt) return null;

        string methodPart = typeName[..lt];
        int lastDot = methodPart.LastIndexOf('.');
        return lastDot >= 0 ? typeName[(lastDot + 1)..(gt + 1)] + '>' : typeName[..(gt + 1)] + '>';
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
