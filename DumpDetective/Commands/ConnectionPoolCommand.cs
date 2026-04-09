using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class ConnectionPoolCommand
{
    private const string Help = """
        Usage: DumpDetective connection-pool <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses
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
        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var found = new List<(string Type, ulong Addr)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning connection objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (ConnectionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    found.Add((name, obj.Address));
            }
        });

        var grouped = found.GroupBy(f => f.Type).OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0") }).ToList();

        sink.Section("DB Connection Objects");
        if (found.Count == 0) { sink.Text("No database connection objects found."); return; }
        sink.Table(["Type", "Count"], grouped);

        if (showAddr)
        {
            var rows = found.Take(100).Select(f => new[] { f.Type, $"0x{f.Addr:X16}" }).ToList();
            sink.Table(["Type", "Address"], rows);
        }

        if (found.Count > 50) sink.Alert(AlertLevel.Critical, $"{found.Count:N0} DB connection objects on heap.",
            advice: "Wrap connections in 'using'. Verify connection pool settings.");
        else if (found.Count > 10) sink.Alert(AlertLevel.Warning, $"{found.Count:N0} DB connection objects on heap.");
        sink.KeyValues([("Total connections", found.Count.ToString("N0"))]);
    }
}
