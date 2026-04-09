using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace DumpDetective.Commands;

internal static partial class ConnectionPoolCommand
{
    private const string Help = """
        Usage: DumpDetective connection-pool <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses (up to 200)
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

        var found = new List<(string Type, ulong Addr, long Size, string State, string ConnStr)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning connection objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!MatchesPrefix(name)) continue;

                long   size    = (long)obj.Size;
                string state   = ReadConnectionState(obj);
                string connStr = ReadMaskedConnStr(obj);

                found.Add((name, obj.Address, size, state, connStr));
            }
        });

        sink.Section("Summary");
        if (found.Count == 0) { sink.Text("No database connection objects found."); return; }

        long totalSize = found.Sum(f => f.Size);
        sink.KeyValues([
            ("Total connection objects",  found.Count.ToString("N0")),
            ("Total size",                DumpHelpers.FormatSize(totalSize)),
        ]);

        if (found.Count > 50)
            sink.Alert(AlertLevel.Critical, $"{found.Count:N0} DB connection objects on heap.",
                advice: "Wrap connections in 'using'. Verify connection pool MaxPoolSize. Do not store DbContext in static fields.");
        else if (found.Count > 10)
            sink.Alert(AlertLevel.Warning, $"{found.Count:N0} DB connection objects on heap.");

        // Type × State breakdown
        var byTypeState = found
            .GroupBy(f => (f.Type, f.State))
            .OrderByDescending(g => g.Count())
            .Select(g => new[]
            {
                g.Key.Type,
                g.Key.State.Length > 0 ? g.Key.State : "—",
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(f => f.Size)),
            })
            .ToList();
        sink.Table(["Type", "State", "Count", "Size"], byTypeState, "Type × State breakdown");

        // Connection string summary (masked)
        var connStrGroups = found
            .Where(f => f.ConnStr.Length > 0)
            .GroupBy(f => f.ConnStr)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (connStrGroups.Count > 0)
            sink.Table(["Connection String (masked)", "Count"], connStrGroups, "Distinct connection strings (passwords masked)");

        if (showAddr)
        {
            var addrRows = found.Take(200)
                .Select(f => new[]
                {
                    $"0x{f.Addr:X16}",
                    f.Type,
                    f.State.Length > 0 ? f.State : "—",
                    DumpHelpers.FormatSize(f.Size),
                })
                .ToList();
            sink.Table(["Address", "Type", "State", "Size"], addrRows);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static bool MatchesPrefix(string name)
    {
        foreach (var p in ConnectionPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ReadConnectionState(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        // Try common field names for connection state integer
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

    static string ReadMaskedConnStr(Microsoft.Diagnostics.Runtime.ClrObject obj)
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
