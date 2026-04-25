using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using System.Text.RegularExpressions;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Collects full detail for all live ADO.NET / ORM connection objects.
/// Implements <see cref="IHeapObjectConsumer"/> for pre-warming via <c>DumpCollector</c>
/// or drives its own heap walk via <see cref="Analyze"/>.
/// </summary>
public sealed partial class ConnectionPoolAnalyzer : IHeapObjectConsumer
{
    private List<ConnectionInfo>?  _connections;
    private List<DbCommandEntry>?   _commands;
    private ConnectionPoolData? _result;

    public ConnectionPoolData? Result => _result;

    private static readonly string[] CommandTypePrefixes =
    [
        "System.Data.SqlClient.SqlCommand",
        "Microsoft.Data.SqlClient.SqlCommand",
        "System.Data.OleDb.OleDbCommand",
        "System.Data.Odbc.OdbcCommand",
        "Npgsql.NpgsqlCommand",
        "MySql.Data.MySqlClient.MySqlCommand",
        "Microsoft.EntityFrameworkCore.Storage.RelationalCommand",
    ];

    internal void Reset()
    {
        _connections = new List<ConnectionInfo>();
        _commands    = new List<DbCommandEntry>();
        _result      = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (_connections is null) return;

        if (meta.IsConnection)
        {
            _connections.Add(new ConnectionInfo(
                meta.Name,
                obj.Address,
                (long)obj.Size,
                ReadConnectionState(obj),
                ReadMaskedConnStr(obj)));
        }
        else if (_commands is not null && IsDbCommand(meta.Name))
        {
            try
            {
                string? text = TryReadCommandText(obj);
                if (!string.IsNullOrWhiteSpace(text))
                    _commands.Add(new DbCommandEntry(meta.Name, text!.Trim()));
            }
            catch { }
        }
    }

    public void OnWalkComplete()
        => _result = new ConnectionPoolData(
               (_connections ?? []).ToList(),
               (_commands   ?? []).ToList());

    public IHeapObjectConsumer CreateClone()
    {
        var c = new ConnectionPoolAnalyzer();
        c.Reset();
        return c;
    }

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (ConnectionPoolAnalyzer)other;
        if (src._connections is not null) (_connections ??= []).AddRange(src._connections);
        if (src._commands    is not null) (_commands    ??= []).AddRange(src._commands);
    }

    // ── Command entry point ───────────────────────────────────────────────────

    public ConnectionPoolData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<ConnectionPoolData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning connection objects...", update =>
            HeapWalker.Walk(ctx.Heap, [this], CommandBase.StatusProgress(update)));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadConnectionState(in ClrObject obj)
    {
        int state = -1;
        foreach (var field in new[] { "_state", "_connectionState", "_objectState" })
        {
            try { state = obj.ReadField<int>(field); break; } catch { }
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

    private static string ReadMaskedConnStr(in ClrObject obj)
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

    private static string MaskPassword(string cs) =>
        PasswordPattern().Replace(cs, m => m.Value[..m.Value.IndexOf('=')] + "=***");

    private static bool IsDbCommand(string typeName)
    {
        foreach (var pfx in CommandTypePrefixes)
            if (typeName.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? TryReadCommandText(in ClrObject obj)
    {
        foreach (var field in new[] { "_commandText", "CommandText", "_cmdText", "_text" })
        {
            try
            {
                string? text = obj.ReadStringField(field);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch { }
        }
        return null;
    }
}
