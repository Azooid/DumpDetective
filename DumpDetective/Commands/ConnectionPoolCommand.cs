using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace DumpDetective.Commands;

internal static partial class ConnectionPoolCommand
{
    private const string Help = """
        Usage: DumpDetective connection-pool <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses and call stacks per connection
          -o, --output <f>   Write report to file
          -h, --help         Show this help

        Notes:
          When --addresses is passed, each connection object that can be traced to
          a thread stack shows the full managed call stack of that thread — revealing
          exactly where the connection was opened or is currently being used.
        """;

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

    // Per-connection enriched record
    private sealed record ConnectionInfo(
        string   TypeName,
        ulong    Addr,
        long     Size,
        string   State,
        string   ConnStr,
        int?     ThreadId,
        uint     OSThreadId,
        string   RootKind,
        List<string> Stack);

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
            "Dump Detective — DB Connection Pool Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        // ── Phase 1: collect all connection objects ───────────────────────────
        var rawFound = new List<(string Type, ulong Addr, long Size, string State, string ConnStr)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning connection objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!MatchesPrefix(obj.Type.Name ?? string.Empty)) continue;
                rawFound.Add((
                    obj.Type.Name!,
                    obj.Address,
                    (long)obj.Size,
                    ReadConnectionState(obj),
                    ReadMaskedConnStr(obj)));
            }
        });

        if (rawFound.Count == 0) { sink.Text("No database connection objects found."); return; }
        var connAddrs = rawFound.Select(f => f.Addr).ToHashSet();

        // ── Phase 2: build thread stack-range map ─────────────────────────────
        // For stack-rooted connections we need: thread id, OS thread id, stack frames
        var threadInfo = ctx.Runtime.Threads
            .Where(t => t.IsAlive && t.StackBase != 0 && t.StackLimit != 0)
            .Select(t => (
                Thread: t,
                Lo: Math.Min(t.StackBase, t.StackLimit),
                Hi: Math.Max(t.StackBase, t.StackLimit)))
            .ToList();

        // addr → (managedThreadId, osThreadId)
        var addrToThread = new Dictionary<ulong, (int MgdId, uint OsId)>();
        // managedThreadId → stack frames (lazy – built on demand)
        var threadStacks  = new Dictionary<int, List<string>>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Tracing GC roots for connections...", _ =>
        {
            foreach (var root in ctx.Heap.EnumerateRoots())
            {
                if (!connAddrs.Contains(root.Object)) continue;

                string rootKind = root.RootKind switch
                {
                    ClrRootKind.Stack             => "Stack",
                    ClrRootKind.StrongHandle      => "Strong Handle",
                    ClrRootKind.PinnedHandle      => "Pinned Handle",
                    ClrRootKind.AsyncPinnedHandle  => "Async-Pinned",
                    ClrRootKind.RefCountedHandle   => "RefCount Handle",
                    ClrRootKind.FinalizerQueue     => "Finalizer Queue",
                    _                              => root.RootKind.ToString(),
                };

                // For stack roots — identify thread by slot address range
                if (root.RootKind == ClrRootKind.Stack && root.Address != 0)
                {
                    foreach (var (thread, lo, hi) in threadInfo)
                    {
                        if (root.Address < lo || root.Address > hi) continue;

                        addrToThread[root.Object] = (thread.ManagedThreadId, thread.OSThreadId);

                        // Collect stack frames for this thread if not already done
                        if (!threadStacks.ContainsKey(thread.ManagedThreadId))
                        {
                            threadStacks[thread.ManagedThreadId] = thread
                                .EnumerateStackTrace()
                                .Select(f => f.FrameName ?? f.Method?.Signature ?? f.ToString() ?? "?")
                                .Where(s => s.Length > 0 && s != "?")
                                .Take(30)
                                .ToList();
                        }
                        break;
                    }
                }
                else if (!addrToThread.ContainsKey(root.Object))
                {
                    // Mark as non-stack root so we don't re-tag it as unknown
                    addrToThread[root.Object] = (-1, 0);
                }
            }
        });

        // ── Phase 3: assemble enriched records ───────────────────────────────
        var connections = rawFound.Select(f =>
        {
            addrToThread.TryGetValue(f.Addr, out var tInfo);
            int? threadId = tInfo.MgdId > 0 ? tInfo.MgdId : null;
            List<string> stack = threadId.HasValue && threadStacks.TryGetValue(threadId.Value, out var s) ? s : [];
            string rootKind = tInfo.MgdId == -1 ? "Non-stack root"
                            : tInfo.MgdId ==  0 ? "No known root"
                            : "Stack";
            return new ConnectionInfo(f.Type, f.Addr, f.Size, f.State, f.ConnStr,
                                      threadId, tInfo.OsId, rootKind, stack);
        }).ToList();

        // ── Summary ───────────────────────────────────────────────────────────
        sink.Section("Summary");
        long totalSize   = connections.Sum(c => c.Size);
        int  stackRooted = connections.Count(c => c.ThreadId.HasValue);
        int  noRoot      = connections.Count(c => c.RootKind == "No known root");

        const int DefaultMaxPool = 100;
        var poolGroups = connections
            .GroupBy(c => (c.TypeName, c.ConnStr.Length > 0 ? c.ConnStr : "<no-connstr>"))
            .Select(g =>
            {
                int active  = g.Count(c => c.State is "Open" or "Executing" or "Fetching");
                int total   = g.Count();
                double util = total > 0 ? active * 100.0 / DefaultMaxPool : 0;
                return (TypeKey: g.Key.Item1, ConnKey: g.Key.Item2, Active: active, Total: total, UtilPct: util);
            })
            .OrderByDescending(p => p.UtilPct)
            .ToList();

        sink.KeyValues([
            ("Total connection objects",      connections.Count.ToString("N0")),
            ("Total size",                    DumpHelpers.FormatSize(totalSize)),
            ("Pool groups (type+connstr)",    poolGroups.Count.ToString("N0")),
            ("Live on thread stacks",         stackRooted.ToString("N0")),
            ("No known GC root",              noRoot.ToString("N0")),
        ]);

        if (connections.Count > 50)
            sink.Alert(AlertLevel.Critical, $"{connections.Count:N0} DB connection objects on heap.",
                advice: "Wrap connections in 'using'. Verify connection pool MaxPoolSize. Do not store DbContext in static fields.");
        else if (connections.Count > 10)
            sink.Alert(AlertLevel.Warning, $"{connections.Count:N0} DB connection objects on heap.");

        // ── Type × State table ────────────────────────────────────────────────
        var byTypeState = connections
            .GroupBy(c => (c.TypeName, c.State))
            .OrderByDescending(g => g.Count())
            .Select(g => new[]
            {
                g.Key.TypeName,
                g.Key.State.Length > 0 ? g.Key.State : "—",
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(c => c.Size)),
            })
            .ToList();
        sink.Table(["Type", "State", "Count", "Size"], byTypeState, "Type × State breakdown");

        // ── Pool utilization ──────────────────────────────────────────────────
        if (poolGroups.Count > 0)
        {
            var utilRows = poolGroups.Select(p => new[]
            {
                p.TypeKey.Contains('.') ? p.TypeKey[(p.TypeKey.LastIndexOf('.') + 1)..] : p.TypeKey,
                p.ConnKey.Length > 60 ? p.ConnKey[..57] + "…" : p.ConnKey,
                p.Total.ToString("N0"),
                p.Active.ToString("N0"),
                $"{p.UtilPct:F0}% of {DefaultMaxPool}",
            }).ToList();
            sink.Table(
                ["Type", "Connection String", "Total", "Active", "Pool Utilization"],
                utilRows,
                $"Pool utilization vs default MaxPoolSize={DefaultMaxPool}");

            var highUtil = poolGroups.Where(p => p.UtilPct >= 80).ToList();
            if (highUtil.Count > 0)
                sink.Alert(AlertLevel.Critical,
                    $"{highUtil.Count} connection pool(s) at ≥80% utilization.",
                    "Near-full pools cause connection wait timeouts and request queuing.",
                    "Increase MaxPoolSize, reduce connection lifetime, or audit for unreleased connections.");
        }

        // ── Connection string table ───────────────────────────────────────────
        var connStrGroups = connections
            .Where(c => c.ConnStr.Length > 0)
            .GroupBy(c => c.ConnStr)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (connStrGroups.Count > 0)
            sink.Table(["Connection String (masked)", "Count"], connStrGroups, "Distinct connection strings (passwords masked)");

        // ── Per-connection detail with call stacks ────────────────────────────
        if (showAddr)
        {
            sink.Section("Per-Connection Detail & Call Stacks");
            sink.Alert(AlertLevel.Info,
                "Call stacks show the managed thread that holds this connection object on its stack at the time of the dump.",
                "A 'No known root' connection is not referenced from any thread stack — it may be pooled, leaked, or held by a static field.");

            int idx = 0;
            foreach (var c in connections.Take(200))
            {
                idx++;
                bool hasStack = c.Stack.Count > 0;

                string title = $"#{idx}  {c.TypeName}  @ 0x{c.Addr:X16}";
                if (c.State.Length > 0)          title += $"  [{c.State}]";
                if (c.ThreadId.HasValue)          title += $"  Thread {c.ThreadId} (OS: 0x{c.OSThreadId:X})";
                else                              title += $"  [{c.RootKind}]";

                // Open by default if: "Open"/"Executing" state or on a thread
                bool open = c.State is "Open" or "Executing" or "Fetching" || c.ThreadId.HasValue;
                sink.BeginDetails(title, open: open);

                sink.KeyValues([
                    ("Type",             c.TypeName),
                    ("Address",          $"0x{c.Addr:X16}"),
                    ("State",            c.State.Length > 0 ? c.State : "—"),
                    ("Size",             DumpHelpers.FormatSize(c.Size)),
                    ("Connection str",   c.ConnStr.Length > 0 ? c.ConnStr : "—"),
                    ("GC root kind",     c.RootKind),
                    ("Thread (managed)", c.ThreadId.HasValue  ? $"Thread {c.ThreadId}" : "—"),
                    ("Thread (OS)",      c.OSThreadId != 0    ? $"0x{c.OSThreadId:X}"  : "—"),
                ]);

                if (hasStack)
                {
                    // Highlight connection-related frames
                    var frameRows = c.Stack.Select((f, i) =>
                    {
                        bool isConnFrame = f.Contains("Connection",  StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("Execute",     StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("Open",        StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("DbContext",   StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("Repository",  StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("DataAccess",  StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("SqlCommand",  StringComparison.OrdinalIgnoreCase)
                                        || f.Contains("Query",       StringComparison.OrdinalIgnoreCase);
                        string marker = isConnFrame ? "►" : " ";
                        return new[] { i.ToString(), marker, f };
                    }).ToList();
                    sink.Table(["#", "", "Stack Frame"], frameRows, $"Thread {c.ThreadId} — {c.Stack.Count} frame(s)  (► = connection-related)");
                }
                else if (c.ThreadId.HasValue)
                {
                    sink.Text("  (no managed frames available for this thread)");
                }
                else
                {
                    sink.Text($"  Not held on any thread stack — root kind: {c.RootKind}.");
                    sink.Text("  The connection may be stored in a static field, cache, or returned to the pool.");
                }

                sink.EndDetails();
            }

            if (connections.Count > 200)
                sink.Alert(AlertLevel.Info, $"Showing first 200 of {connections.Count:N0} connection objects.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static bool MatchesPrefix(string name)
    {
        foreach (var p in ConnectionPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ReadConnectionState(ClrObject obj)
    {
        int state = -1;
        foreach (var field in new[] { "_state", "_connectionState", "_objectState" })
        {
            try { state = obj.ReadField<int>(field); break; }
            catch { }
        }
        return state switch
        {
             0 => "Closed",
             1 => "Open",
            16 => "Connecting",
            32 => "Executing",
            64 => "Fetching",
           256 => "Broken",
            -1 => "",
             _ => state.ToString(),
        };
    }

    static string ReadMaskedConnStr(ClrObject obj)
    {
        foreach (var field in new[] { "_connectionString", "ConnectionString", "_connectionStringBuilder" })
        {
            try
            {
                string? cs = obj.ReadStringField(field);
                if (!string.IsNullOrEmpty(cs))
                    return MaskPassword(cs!);
            }
            catch { }
        }
        return "";
    }

    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();

    static string MaskPassword(string cs) =>
        PasswordPattern().Replace(cs, m => m.Value.Split('=')[0] + "=***");
}
