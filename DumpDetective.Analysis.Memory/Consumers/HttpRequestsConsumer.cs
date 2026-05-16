using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Consumers;

/// <summary>
/// Accumulates <see cref="HttpObjectEntry"/> instances for <c>HttpRequestsAnalyzer</c>.
/// Pre-populated during <c>CollectHeapObjectsCombined</c> and cached via
/// <c>DumpContext.SetAnalysis&lt;HttpRequestsData&gt;</c>.
/// Field names are probed with fallbacks to cover both .NET Framework and .NET Core/5+.
/// </summary>
internal sealed class HttpRequestsConsumer : IHeapObjectConsumer
{
    private readonly List<HttpObjectEntry>    _entries       = [];
    private readonly List<ServicePointEntry>  _servicePoints = [];

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
            switch (meta.Name)
            {
                case "System.Net.Http.HttpRequestMessage":
                    // .NET Framework field names (no underscore prefix)
                    var methodObj = obj.ReadObjectField("method");
                    if (!methodObj.IsValid) methodObj = obj.ReadObjectField("_method"); // .NET Core fallback
                    if (methodObj.IsValid)
                    {
                        // HttpMethod.method (Fx) or HttpMethod._method (Core)
                        var methodStr = methodObj.ReadObjectField("method");
                        if (!methodStr.IsValid) methodStr = methodObj.ReadObjectField("_method");
                        method = methodStr.IsValid ? (methodStr.AsString() ?? "") : "";
                    }
                    // .NET Framework: requestUri  |  .NET Core: _requestUri
                    var uriObj = obj.ReadObjectField("requestUri");
                    if (!uriObj.IsValid) uriObj = obj.ReadObjectField("_requestUri");
                    uri = ReadUriFromUriObject(uriObj);
                    break;

                case "System.Net.HttpWebRequest":
                    // _Verb is System.Net.KnownHttpVerb with a Name string field
                    var verbObj = obj.ReadObjectField("_Verb");
                    if (verbObj.IsValid)
                    {
                        var verbName = verbObj.ReadObjectField("Name");
                        method = verbName.IsValid ? (verbName.AsString() ?? "") : "";
                    }
                    uri = ReadUriFromUriObject(obj.ReadObjectField("_Uri"));
                    break;

                case "System.Net.Http.HttpResponseMessage":
                    statusCode = obj.ReadField<int>("_statusCode");
                    break;

                case "System.Net.ServicePoint":
                    CollectServicePoint(in obj);
                    return;  // goes into _servicePoints, not _entries
            }
        }
        catch { }

        _entries.Add(new HttpObjectEntry(meta.Name, obj.Address, size, method, uri, statusCode));
    }

    /// <summary>
    /// Reads the URI string from a <c>System.Uri</c> object, trying both
    /// .NET Core (<c>_string</c>) and .NET Framework (<c>m_String</c>) field names,
    /// with a final fallback to <c>AbsoluteUri</c> if present.
    /// </summary>
    private static string ReadUriFromUriObject(ClrObject uriObj)
    {
        if (!uriObj.IsValid || uriObj.Type is null) return "";
        // Check field existence first — ReadObjectField throws if the field is not defined
        // on this type (differs between .NET Framework and .NET Core/5+ System.Uri).
        foreach (var fn in (ReadOnlySpan<string>)["_string", "m_String", "m_originalUnicodeString"])
        {
            if (uriObj.Type.GetFieldByName(fn) is null) continue;
            try
            {
                var f = uriObj.ReadObjectField(fn);
                if (f.IsValid) { var s = f.AsString(); if (!string.IsNullOrEmpty(s)) return s; }
            }
            catch { }
        }
        return "";
    }

    private static string TryReadString(in ClrObject obj, string field)
    {
        try
        {
            var f = obj.ReadObjectField(field);
            return f.IsValid ? (f.AsString() ?? "") : "";
        }
        catch { return ""; }
    }

    private void CollectServicePoint(in ClrObject obj)
    {
        try
        {
            string address = ReadUriFromUriObject(obj.ReadObjectField("m_Address"));
            string host    = TryReadString(in obj, "m_Host");
            int    port    = 0, currentConns = 0, connLimit = 2;
            try { port         = obj.ReadField<int>("m_Port"); }               catch { }
            try { currentConns = obj.ReadField<int>("m_CurrentConnections"); } catch { }
            try { connLimit    = obj.ReadField<int>("m_ConnectionLimit"); }    catch { }
            _servicePoints.Add(new ServicePointEntry(obj.Address, address, host, port, currentConns, connLimit));
        }
        catch { }
    }

    public void OnWalkComplete()
    {
        Result = new HttpRequestsData(_entries, ServicePoints: _servicePoints);
    }

    public IHeapObjectConsumer CreateClone() => new HttpRequestsConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (HttpRequestsConsumer)other;
        _entries.AddRange(src._entries);
        _servicePoints.AddRange(src._servicePoints);
    }
}
