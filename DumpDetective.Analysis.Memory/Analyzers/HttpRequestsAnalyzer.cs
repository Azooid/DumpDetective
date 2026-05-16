using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Reports all live <c>System.Net.Http.*</c> and <c>System.Net.HttpWebRequest</c>
/// objects on the heap, showing method, URI, and status code where available.
/// Fast path: returns the <see cref="Consumers.HttpRequestsConsumer"/> result cached
///   by <c>DumpCollector.CollectHeapObjectsCombined</c> — no second heap walk.
/// Slow path: walks the heap filtering by the known HTTP type set, then attempts
///   field reads for method/URI/status (all guarded with try/catch).
/// </summary>
public sealed class HttpRequestsAnalyzer
{
    private static readonly HashSet<string> HttpTypeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Net.Http.HttpRequestMessage",
        "System.Net.Http.HttpResponseMessage",
        "System.Net.HttpWebRequest",
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpClientHandler",
        "System.Net.Http.SocketsHttpHandler",
        "System.Net.ServicePoint",
    };

    public HttpRequestsData Analyze(DumpContext ctx)
    {
        // Fast path: pre-populated by HttpRequestsConsumer during CollectHeapObjectsCombined.
        var cached = ctx.GetAnalysis<HttpRequestsData>();
        if (cached is not null)
        {
            // Still enrich with async correlations (may not have been built at collection time).
            if (cached.AsyncCorrelations is { Count: 0 } or null)
            {
                var corrs = BuildAsyncCorrelations(ctx, cached.Objects);
                return cached with { AsyncCorrelations = corrs };
            }
            return cached;
        }

        var found         = new List<HttpObjectEntry>();
        var servicePoints = new List<ServicePointEntry>();

        CommandBase.RunStatus("Scanning HTTP objects...", update =>
        {
            long count = 0;
            var  sw    = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                count++;
                if ((count & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning HTTP objects \u2014 {count:N0} objects  \u2022  {found.Count} HTTP objects found...");
                    sw.Restart();
                }
                var name = obj.Type.Name ?? string.Empty;
                if (!HttpTypeSet.Contains(name)) continue;

                long   size       = (long)obj.Size;
                string method     = "";
                string uri        = "";
                int    statusCode = 0;

                try
                {
                    if (name == "System.Net.Http.HttpRequestMessage")
                    {
                        // .NET Framework: method / requestUri  |  .NET Core: _method / _requestUri
                        var methodObj = obj.ReadObjectField("method");
                        if (!methodObj.IsValid) methodObj = obj.ReadObjectField("_method");
                        if (methodObj.IsValid)
                        {
                            // HttpMethod.method (Fx) or HttpMethod._method (Core)
                            var methodStr = methodObj.ReadObjectField("method");
                            if (!methodStr.IsValid) methodStr = methodObj.ReadObjectField("_method");
                            method = methodStr.IsValid ? (methodStr.AsString() ?? "") : "";
                        }
                        var uriObj = obj.ReadObjectField("requestUri");
                        if (!uriObj.IsValid) uriObj = obj.ReadObjectField("_requestUri");
                        uri = ReadUriFromUriObject(uriObj);
                    }
                    else if (name == "System.Net.HttpWebRequest")
                    {
                        // _Verb is System.Net.KnownHttpVerb with a Name string field
                        var verbObj = obj.ReadObjectField("_Verb");
                        if (verbObj.IsValid)
                        {
                            var verbName = verbObj.ReadObjectField("Name");
                            method = verbName.IsValid ? (verbName.AsString() ?? "") : "";
                        }
                        uri = ReadUriFromUriObject(obj.ReadObjectField("_Uri"));
                    }
                    else if (name == "System.Net.Http.HttpResponseMessage")
                    {
                        statusCode = obj.ReadField<int>("_statusCode");
                    }
                    else if (name == "System.Net.ServicePoint")
                    {
                        string addr = ReadUriFromUriObject(obj.ReadObjectField("m_Address"));
                        string host = TryReadStringField(in obj, "m_Host");
                        int    port = 0, currentConns = 0, connLimit = 2;
                        try { port         = obj.ReadField<int>("m_Port"); }               catch { }
                        try { currentConns = obj.ReadField<int>("m_CurrentConnections"); } catch { }
                        try { connLimit    = obj.ReadField<int>("m_ConnectionLimit"); }    catch { }
                        servicePoints.Add(new ServicePointEntry(obj.Address, addr, host, port, currentConns, connLimit));
                        continue; // don't add to found entries
                    }
                }
                catch { }

                found.Add(new HttpObjectEntry(name, obj.Address, size, method, uri, statusCode));
            }
        });

        return new HttpRequestsData(found, BuildAsyncCorrelations(ctx, found), servicePoints);
    }

    /// <summary>
    /// Cross-references the HTTP object list with any <see cref="AsyncStacksData"/>
    /// already cached in the context. Async state machines whose method names suggest
    /// they are serving HTTP requests are returned as correlation hints.
    /// </summary>
    private static IReadOnlyList<AsyncHttpCorrelation> BuildAsyncCorrelations(
        DumpContext ctx, IReadOnlyList<HttpObjectEntry> httpObjects)
    {
        var asyncData = ctx.GetAnalysis<AsyncStacksData>();
        if (asyncData is null || asyncData.Entries.Count == 0)
            return [];

        // Heuristic: method names that suggest HTTP request-handling context.
        static bool IsHttpLike(string method) =>
            method.Contains("Controller",   StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Handler",      StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Endpoint",     StringComparison.OrdinalIgnoreCase) ||
            method.Contains("HttpRequest",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("HttpContext",  StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Middleware",   StringComparison.OrdinalIgnoreCase) ||
            method.Contains("ProcessRequest", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("InvokeAsync",  StringComparison.OrdinalIgnoreCase);

        // Build a hint from the in-flight request list if any URIs are available.
        var uris = httpObjects
            .Where(o => o.Uri.Length > 0)
            .Select(o => o.Uri)
            .Distinct()
            .Take(3)
            .ToList();
        string uriHint = uris.Count > 0 ? $"in-flight: {string.Join(", ", uris)}" : "no URI data in dump";

        return asyncData.Entries
            .Where(e => e.State == "Suspended" && IsHttpLike(e.Method))
            .Select(e => new AsyncHttpCorrelation(
                e.Method,
                e.State,
                e.Addr,
                uriHint))
            .ToList();
    }

    /// <summary>
    /// Reads the URI string from a <c>System.Uri</c> object, trying multiple
    /// backing field names to cover both .NET Framework and .NET Core/5+.
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

    private static string TryReadStringField(in ClrObject obj, string fieldName)
    {
        try
        {
            var f = obj.ReadObjectField(fieldName);
            return f.IsValid ? (f.AsString() ?? "") : "";
        }
        catch { return ""; }
    }
}
