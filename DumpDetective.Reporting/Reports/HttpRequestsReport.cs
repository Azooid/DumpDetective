using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class HttpRequestsReport
{
    public void Render(HttpRequestsData data, IRenderSink sink, bool showAddr = false)
    {
        sink.Section("Summary");
        if (data.Objects.Count == 0) { sink.Text("No HTTP objects found."); return; }

        sink.Explain(
            what: "Inventories HttpClient, HttpRequestMessage, HttpResponseMessage and related HTTP objects found on the managed heap.",
            why: "HttpClient manages a socket connection pool. Each distinct instance opens its own pool. Creating one per request quickly exhausts available sockets.",
            impact: "Too many HttpClient instances cause SocketException (port exhaustion) and TIME_WAIT socket accumulation in production.",
            bullets: ["HttpClient/Handler count > 1 is a code smell; > 5 is a confirmed leak", "In-Flight Requests section shows URIs/methods of requests pending at dump time", "Non-2xx response codes indicate the application is retrying failed requests"],
            action: "Register a single HttpClient via IHttpClientFactory (typed/named client) or use a static shared HttpClient with a SocketsHttpHandler that has PooledConnectionLifetime set."
        );
        int clientCount = data.Objects.Count(o =>
            o.Type is "System.Net.Http.HttpClient" or
                      "System.Net.Http.HttpClientHandler" or
                      "System.Net.Http.SocketsHttpHandler");

        var summary = data.Objects
            .GroupBy(o => o.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0"), DumpHelpers.FormatSize(g.Sum(o => o.Size)) })
            .ToList();
        sink.Table(["Type", "Count", "Size"], summary);
        sink.KeyValues([("Total HTTP objects", data.Objects.Count.ToString("N0"))]);

        // HttpClient leak alert
        if (clientCount > 5)
            sink.Alert(AlertLevel.Critical, $"{clientCount} HttpClient/Handler instances found.",
                "HttpClient instances should be reused — creating one per request exhausts socket connections.",
                "Use IHttpClientFactory or a static/singleton HttpClient.");
        else if (clientCount > 1)
            sink.Alert(AlertLevel.Warning, $"{clientCount} HttpClient/Handler instances found.");

        RenderRequestDetails(sink, data.Objects);
        RenderResponseCodes(sink, data.Objects);
        if (showAddr) RenderAddresses(sink, data.Objects);
    }

    private static void RenderRequestDetails(IRenderSink sink, IReadOnlyList<HttpObjectEntry> objects)
    {
        var requests = objects.Where(o =>
            o.Type is "System.Net.Http.HttpRequestMessage" or "System.Net.HttpWebRequest").ToList();
        if (requests.Count == 0) return;

        sink.Section("In-Flight Requests");
        var byHost = requests
            .GroupBy(r => ExtractHost(r.Uri))
            .OrderByDescending(g => g.Count())
            .Select(g => new[] { g.Key, g.Count().ToString("N0"),
                string.Join(", ", g.Select(r => r.Method).Distinct().Take(5)) })
            .ToList();
        if (byHost.Count > 0)
            sink.Table(["Host", "Count", "Methods"], byHost);
    }

    private static void RenderResponseCodes(IRenderSink sink, IReadOnlyList<HttpObjectEntry> objects)
    {
        var responses = objects.Where(o => o.StatusCode > 0).ToList();
        if (responses.Count == 0) return;

        sink.Section("Response Status Codes");
        var rows = responses
            .GroupBy(o => o.StatusCode)
            .OrderBy(g => g.Key)
            .Select(g => new[] { g.Key.ToString(), g.Count().ToString("N0"), StatusCategory(g.Key) })
            .ToList();
        sink.Table(["Status Code", "Count", "Category"], rows);
    }

    private static void RenderAddresses(IRenderSink sink, IReadOnlyList<HttpObjectEntry> objects)
    {
        sink.Section("Object Addresses (up to 200)");
        var rows = objects.Take(200)
            .Select(o => new[] { o.Type, $"0x{o.Addr:X16}", DumpHelpers.FormatSize(o.Size), o.Method, o.Uri }).ToList();
        sink.Table(["Type", "Address", "Size", "Method", "URI"], rows);
    }

    private static string ExtractHost(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "(no URI)";
        try
        {
            var u = new Uri(uri);
            return u.Host;
        }
        catch { return uri.Length > 60 ? uri[..57] + "…" : uri; }
    }

    private static string StatusCategory(int code) => code switch
    {
        >= 200 and < 300 => "Success",
        >= 300 and < 400 => "Redirect",
        >= 400 and < 500 => "Client Error",
        >= 500 => "Server Error",
        _ => "",
    };
}
