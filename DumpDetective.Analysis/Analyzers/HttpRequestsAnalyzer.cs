using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

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
    };

    public HttpRequestsData Analyze(DumpContext ctx)
    {
        // Fast path: pre-populated by HttpRequestsConsumer during CollectHeapObjectsCombined.
        var cached = ctx.GetAnalysis<HttpRequestsData>();
        if (cached is not null) return cached;

        var found = new List<HttpObjectEntry>();

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
                        var methodObj = obj.ReadObjectField("_method");
                        if (methodObj.IsValid)
                        {
                            var methodStr = methodObj.ReadObjectField("_method");
                            method = methodStr.IsValid ? (methodStr.AsString() ?? "") : "";
                        }
                        var uriObj = obj.ReadObjectField("_requestUri");
                        if (uriObj.IsValid) uri = uriObj.AsString() ?? "";
                    }
                    else if (name == "System.Net.Http.HttpResponseMessage")
                    {
                        statusCode = obj.ReadField<int>("_statusCode");
                    }
                }
                catch { }

                found.Add(new HttpObjectEntry(name, obj.Address, size, method, uri, statusCode));
            }
        });

        return new HttpRequestsData(found);
    }
}
