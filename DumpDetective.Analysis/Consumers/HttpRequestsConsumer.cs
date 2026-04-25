using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Accumulates <see cref="HttpObjectEntry"/> instances for <c>HttpRequestsAnalyzer</c>.
/// Pre-populated during <c>CollectHeapObjectsCombined</c> and cached via
/// <c>DumpContext.SetAnalysis&lt;HttpRequestsData&gt;</c>.
/// For each HTTP object the consumer attempts field reads:
///   - <c>HttpRequestMessage</c>: reads <c>_method._method</c> (string) and <c>_requestUri</c>.
///   - <c>HttpResponseMessage</c>: reads <c>_statusCode</c> (int).
/// All field reads are wrapped in try/catch because CLR field offsets may differ
/// across .NET versions and the dump may contain partially-collected objects.
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

    public IHeapObjectConsumer CreateClone() => new HttpRequestsConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (HttpRequestsConsumer)other;
        _entries.AddRange(src._entries);
    }
}
