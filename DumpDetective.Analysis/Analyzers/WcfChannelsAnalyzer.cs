using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Collects full detail for all live <c>System.ServiceModel.*</c> WCF channel objects.
/// Implements <see cref="IHeapObjectConsumer"/> for pre-warming via <c>DumpCollector</c>
/// or drives its own heap walk via <see cref="Analyze"/>.
/// </summary>
public sealed class WcfChannelsAnalyzer : IHeapObjectConsumer
{
    private List<WcfObjectInfo>? _objects;
    private WcfChannelsData? _result;

    public WcfChannelsData? Result => _result;

    internal void Reset()
    {
        _objects = new List<WcfObjectInfo>();
        _result  = null;
    }

    // ── IHeapObjectConsumer ───────────────────────────────────────────────────

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsWcf || _objects is null) return;
        string state = ReadCommunicationState(obj);
        _objects.Add(new WcfObjectInfo(
            meta.Name,
            obj.Address,
            state,
            ReadEndpointAddress(obj),
            ReadBindingType(obj),
            ReadFaultReason(obj, state)));
    }

    public void OnWalkComplete()
        => _result = new WcfChannelsData((_objects ?? []).ToList());

    // ── Command entry point ───────────────────────────────────────────────────

    public WcfChannelsData Analyze(DumpContext ctx)
    {
        if (ctx.GetAnalysis<WcfChannelsData>() is { } cached) return cached;

        Reset();
        CommandBase.RunStatus("Scanning WCF objects...", update =>
            HeapWalker.Walk(ctx.Heap, [this], CommandBase.StatusProgress(update)));

        ctx.SetAnalysis(_result!);
        return _result!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadCommunicationState(in ClrObject obj)
    {
        int s = -1;
        foreach (var field in new[] { "_state", "_communicationState", "state" })
        {
            try { s = obj.ReadField<int>(field); break; } catch { }
        }
        return s switch
        {
            0 => "Created", 1 => "Opening", 2 => "Opened",
            3 => "Closing", 4 => "Closed",  5 => "Faulted",
            _ => s >= 0 ? s.ToString() : "",
        };
    }

    private static string ReadEndpointAddress(in ClrObject obj)
    {
        foreach (var field in new[] { "_remoteAddress", "_via", "_listenUri", "_address" })
        {
            try
            {
                var uriObj = obj.ReadObjectField(field);
                if (uriObj.IsNull || !uriObj.IsValid) continue;
                try { string? s = uriObj.ReadStringField("_string"); if (!string.IsNullOrEmpty(s)) return s!; } catch { }
                try
                {
                    var innerUri = uriObj.ReadObjectField("_uri");
                    if (!innerUri.IsNull && innerUri.IsValid)
                    {
                        try { string? s = innerUri.ReadStringField("_string"); if (!string.IsNullOrEmpty(s)) return s!; } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }
        return "";
    }

    private static string ReadFaultReason(in ClrObject obj, string state)
    {
        if (state != "Faulted") return "";
        foreach (var field in new[] { "_faultReason", "faultReason", "_closedException" })
        {
            try { string? s = obj.ReadStringField(field); if (!string.IsNullOrEmpty(s)) return s!; } catch { }
            try
            {
                var exObj = obj.ReadObjectField(field);
                if (!exObj.IsNull && exObj.IsValid)
                {
                    string? msg = exObj.ReadStringField("_message");
                    if (!string.IsNullOrEmpty(msg)) return msg!;
                }
            }
            catch { }
        }
        return "";
    }

    private static string ReadBindingType(in ClrObject obj)
    {
        foreach (var field in new[] { "_binding", "binding", "_channelFactory" })
        {
            try
            {
                var bindObj = obj.ReadObjectField(field);
                if (bindObj.IsNull || !bindObj.IsValid) continue;
                string? typeName = bindObj.Type?.Name;
                if (!string.IsNullOrEmpty(typeName))
                {
                    int dot = typeName.LastIndexOf('.');
                    return dot >= 0 ? typeName[(dot + 1)..] : typeName!;
                }
            }
            catch { }
        }
        var name = obj.Type?.Name ?? string.Empty;
        if (name.Contains("BasicHttp",    StringComparison.OrdinalIgnoreCase)) return "BasicHttpBinding";
        if (name.Contains("NetTcp",       StringComparison.OrdinalIgnoreCase)) return "NetTcpBinding";
        if (name.Contains("WSHttp",       StringComparison.OrdinalIgnoreCase)) return "WSHttpBinding";
        if (name.Contains("NetNamedPipe", StringComparison.OrdinalIgnoreCase)) return "NetNamedPipeBinding";
        if (name.Contains("NetMsmq",      StringComparison.OrdinalIgnoreCase)) return "NetMsmqBinding";
        return "";
    }
}
