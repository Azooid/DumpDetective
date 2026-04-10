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
          -a, --addresses    Show object addresses per connection (up to 200)
          -o, --output <f>   Write report to file
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

        var connections = rawFound
            .Select(f => new ConnectionInfo(f.Type, f.Addr, f.Size, f.State, f.ConnStr))
            .ToList();

        // ── Summary ───────────────────────────────────────────────────────────
        sink.Section("Summary");
        long totalSize = connections.Sum(c => c.Size);

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
            ("Total connection objects",   connections.Count.ToString("N0")),
            ("Total size",                 DumpHelpers.FormatSize(totalSize)),
            ("Pool groups (type+connstr)", poolGroups.Count.ToString("N0")),
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

        // ── Per-connection address listing ────────────────────────────────────
        if (showAddr)
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

        // ── Active SQL commands ───────────────────────────────────────────────
        sink.Section("Active SQL Commands");
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

        if (commandTexts.Count == 0)
        {
            sink.Text("No DbCommand objects with readable command text found.");
        }
        else
        {
            var cmdGroups = commandTexts
                .GroupBy(c => c.CommandText.Length > 200 ? c.CommandText[..200] : c.CommandText)
                .Select(g => new[]
                {
                    g.Count().ToString("N0"),
                    g.First().Type.Contains('.') ? g.First().Type[(g.First().Type.LastIndexOf('.') + 1)..] : g.First().Type,
                    g.Key.Length > 120 ? g.Key[..117] + "…" : g.Key,
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
