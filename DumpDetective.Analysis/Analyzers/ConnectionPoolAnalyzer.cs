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
    private List<ConnectionInfo>? _connections;
    private ConnectionPoolData? _result;

    public ConnectionPoolData? Result => _result;

    internal void Reset()
    {
        _connections = new List<ConnectionInfo>();
        _result      = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (_connections is null) return;

        if (!meta.IsConnection) return;

        _connections.Add(new ConnectionInfo(
            meta.Name,
            obj.Address,
            (long)obj.Size,
            ReadConnectionState(obj),
            ReadMaskedConnStr(obj)));
    }

    public void OnWalkComplete()
        => _result = new ConnectionPoolData((_connections ?? []).ToList());

    // ── Command entry point ───────────────────────────────────────────────────

    public ConnectionPoolData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<ConnectionPoolData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning connection objects...", () =>
            HeapWalker.Walk(ctx.Heap, [this]));

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
}
