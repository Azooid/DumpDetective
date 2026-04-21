using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates <see cref="HttpObjectEntry"/> instances for <c>HttpRequestsAnalyzer</c>.
/// Pre-populated during <c>CollectHeapObjectsCombined</c> and cached via
/// <c>DumpContext.SetAnalysis&lt;HttpRequestsData&gt;</c>.
/// </summary>
internal sealed class HttpRequestsConsumer : IHeapObjectConsumer
{
    private readonly List<HttpObjectEntry> _entries = [];

    public HttpRequestsData? Result { get; private set; }

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (!meta.IsHttp) return;

        long   size       = (long)obj.Size;
        string method     = "";
        string uri        = "";
        int    statusCode = 0;

        try
        {
            if (meta.Name == "System.Net.Http.HttpRequestMessage")
            {
                var methodObj = obj.ReadObjectField("_method");
                if (methodObj.IsValid)
                {
                    var methodStr = methodObj.ReadObjectField("_method");
                    method = methodStr.IsValid ? (methodStr.AsString() ?? "") : "";
                }
                var uriObj = obj.ReadObjectField("_requestUri");
                if (uriObj.IsValid) uri = uriObj.AsString() ?? "";
            }
            else if (meta.Name == "System.Net.Http.HttpResponseMessage")
            {
                statusCode = obj.ReadField<int>("_statusCode");
            }
        }
        catch { }

        _entries.Add(new HttpObjectEntry(meta.Name, obj.Address, size, method, uri, statusCode));
    }

    public void OnWalkComplete()
    {
        Result = new HttpRequestsData(_entries);
    }
}
