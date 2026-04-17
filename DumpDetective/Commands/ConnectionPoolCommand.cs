using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace DumpDetective.Commands;

// Scans a heap dump for live ADO.NET / ORM connection and command objects, reports pool
// utilization against the default MaxPoolSize, flags near-exhausted pools, and surfaces
// any CommandText strings still resident on the heap (passwords masked in connection strings).
internal static partial class ConnectionPoolCommand
{
    private const string Help = """
        Usage: DumpDetective connection-pool <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses per connection (up to 200)
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
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

    private sealed record ConnectionInfo(
        string TypeName,
        ulong  Addr,
        long   Size,
        string State,
        string ConnStr);

    // Assumed ADO.NET default MaxPoolSize, used to compute per-pool utilization percentages.
    private const int DefaultMaxPool = 100;

    // Aggregated statistics for one (connection type, connection string) pool bucket.
    private sealed record PoolGroup(
        string TypeKey,
        string ConnKey,
        int Active,      // connections currently in Open, Executing, or Fetching state
        int Total,       // total connections in this bucket regardless of state
        double UtilPct); // Active as a percentage of DefaultMaxPool

    private static readonly string[] CommandTypePrefixes =
    [
        "System.Data.SqlClient.SqlCommand",
        "Microsoft.Data.SqlClient.SqlCommand",
        "System.Data.OleDb.OleDbCommand",
        "System.Data.Odbc.OdbcCommand",
        "Oracle.ManagedDataAccess.Client.OracleCommand",
        "Npgsql.NpgsqlCommand",
        "MySql.Data.MySqlClient.MySqlCommand",
        "Microsoft.EntityFrameworkCore.Storage.RelationalCommand",
    ];

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

        var connections = ScanConnections(ctx);
        if (connections.Count == 0) { sink.Text("No database connection objects found."); return; }

        var poolGroups = BuildPoolGroups(connections);

        RenderSummary(sink, connections, poolGroups);
        RenderTypeStateTable(sink, connections);
        RenderPoolUtilization(sink, poolGroups);
        RenderConnectionStrings(sink, connections);

        if (showAddr)
            RenderAddresses(sink, connections);

        var commandTexts = ScanCommands(ctx);
        RenderCommands(sink, commandTexts);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    // Walks the heap for all known ADO.NET / ORM connection types and reads their state and
    // connection string (with the password already masked) into ConnectionInfo records.
    static List<ConnectionInfo> ScanConnections(DumpContext ctx)
    {
        var connections = new List<ConnectionInfo>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning connection objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                if (!MatchesPrefix(obj.Type.Name ?? string.Empty)) continue;
                connections.Add(new ConnectionInfo(
                    obj.Type.Name!,
                    obj.Address,
                    (long)obj.Size,
                    ReadConnectionState(obj),
                    ReadMaskedConnStr(obj)));
            }
        });
        return connections;
    }

    // Walks the heap for known ADO.NET / ORM command types and reads their CommandText field.
    // Tries several common private field names used across different providers.
    static List<(string Type, string CommandText)> ScanCommands(DumpContext ctx)
    {
        var commandTexts = new List<(string Type, string CommandText)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning DbCommand objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var tname = obj.Type.Name ?? string.Empty;
                bool match = false;
                foreach (var p in CommandTypePrefixes)
                    if (tname.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) continue;

                string cmdText = "";
                foreach (var field in new[] { "_commandText", "CommandText", "_sqlText", "_commandTextTmp", "_text" })
                {
                    try { cmdText = obj.ReadStringField(field) ?? ""; if (cmdText.Length > 0) break; } catch { }
                }
                if (cmdText.Length > 0)
                    commandTexts.Add((tname, cmdText));
            }
        });
        return commandTexts;
    }

    // Groups connections by (type name, connection string) and computes active count and
    // pool utilization relative to DefaultMaxPool.
    static List<PoolGroup> BuildPoolGroups(List<ConnectionInfo> connections) =>
        connections
            .GroupBy(c => (c.TypeName, c.ConnStr.Length > 0 ? c.ConnStr : "<no-connstr>"))
            .Select(g =>
            {
                int active  = g.Count(c => c.State is "Open" or "Executing" or "Fetching");
                int total   = g.Count();
                double util = total > 0 ? active * 100.0 / DefaultMaxPool : 0;
                return new PoolGroup(g.Key.Item1, g.Key.Item2, active, total, util);
            })
            .OrderByDescending(p => p.UtilPct)
            .ToList();

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Overview: total count/size/pool-groups and critical/warning threshold alerts.
    static void RenderSummary(IRenderSink sink, List<ConnectionInfo> connections, List<PoolGroup> poolGroups)
    {
        sink.Section("Summary");
        long totalSize = connections.Sum(c => c.Size);

        sink.KeyValues([
            ("Total connection objects",   connections.Count.ToString("N0")),
            ("Total size",                 DumpHelpers.FormatSize(totalSize)),
            ("Pool groups (type+connstr)", poolGroups.Count.ToString("N0")),
        ]);

        if (connections.Count > 50)
            sink.Alert(AlertLevel.Critical, $"{connections.Count:N0} DB connection objects on heap.",
                advice: "Wrap connections in 'using'. Verify connection pool MaxPoolSize. Do not store DbContext in static fields.");
        else if (connections.Count > 10)
            sink.Alert(AlertLevel.Warning, $"{connections.Count:N0} DB connection objects on heap.");
    }

    // Type × State breakdown: how many connections of each type are in each lifecycle state.
    static void RenderTypeStateTable(IRenderSink sink, List<ConnectionInfo> connections)
    {
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
    }

    // Pool utilization: active vs total per pool bucket, with a critical alert when ≥80% full.
    static void RenderPoolUtilization(IRenderSink sink, List<PoolGroup> poolGroups)
    {
        if (poolGroups.Count == 0) return;

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

    // Top-10 distinct connection strings (passwords already masked) and their usage count.
    static void RenderConnectionStrings(IRenderSink sink, List<ConnectionInfo> connections)
    {
        var connStrGroups = connections
            .Where(c => c.ConnStr.Length > 0)
            .GroupBy(c => c.ConnStr)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (connStrGroups.Count > 0)
            sink.Table(["Connection String (masked)", "Count"], connStrGroups, "Distinct connection strings (passwords masked)");
    }

    // Per-connection address listing for use with WinDbg !dumpobj / !gcroot (up to 200 rows).
    static void RenderAddresses(IRenderSink sink, List<ConnectionInfo> connections)
    {
        sink.Section("Per-Connection Addresses");
        var addrRows = connections.Take(200).Select(c => new[]
        {
            $"0x{c.Addr:X16}",
            c.TypeName,
            c.State.Length > 0 ? c.State : "—",
            DumpHelpers.FormatSize(c.Size),
            c.ConnStr.Length > 0 ? c.ConnStr : "—",
        }).ToList();
        sink.Table(["Address", "Type", "State", "Size", "Connection String (masked)"], addrRows);

        if (connections.Count > 200)
            sink.Alert(AlertLevel.Info, $"Showing first 200 of {connections.Count:N0} connection objects.");
    }

    // Active SQL commands: distinct query texts grouped by frequency, with a leak warning when
    // too many DbCommand objects remain on the heap (indicates they're not being disposed).
    static void RenderCommands(IRenderSink sink, List<(string Type, string CommandText)> commandTexts)
    {
        sink.Section("Active SQL Commands");
        if (commandTexts.Count == 0)
        {
            sink.Text("No DbCommand objects with readable command text found.");
            return;
        }

        var cmdGroups = commandTexts
            .GroupBy(c => c.CommandText)
            .Select(g => new[]
            {
                g.Count().ToString("N0"),
                g.First().Type.Contains('.') ? g.First().Type[(g.First().Type.LastIndexOf('.') + 1)..] : g.First().Type,
                g.Key,
            })
            .OrderByDescending(r => int.Parse(r[0].Replace(",", "")))
            .ToList();
        sink.Table(["Count", "Type", "Command Text"], cmdGroups,
            $"{commandTexts.Count:N0} DbCommand object(s) with command text — {cmdGroups.Count} distinct queries");

        if (commandTexts.Count > 20)
            sink.Alert(AlertLevel.Warning,
                $"{commandTexts.Count:N0} DbCommand objects with command text found on heap.",
                "Commands should be created, executed, and disposed promptly — not cached on the heap.",
                "Use parameterized queries and dispose SqlCommand objects after use.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns true when the type name starts with any of the known connection type prefixes.
    static bool MatchesPrefix(string name)
    {
        foreach (var p in ConnectionPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Reads the integer connection-state field (varies by provider) and maps it to a
    // human-readable label. Returns an empty string when no recognisable field is found.
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

    // Tries several common private field names for the connection string across providers,
    // then masks the password value before returning.
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

    // Replaces the password/pwd value in a connection string with *** using a source-generated regex.
    static string MaskPassword(string cs) =>
        PasswordPattern().Replace(cs, m => m.Value.Split('=')[0] + "=***");
}
