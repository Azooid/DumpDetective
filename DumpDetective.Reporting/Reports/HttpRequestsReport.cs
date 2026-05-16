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

        // ── HTTP object type distribution ────────────────────────────────────
        var typeSegs = data.Objects
            .GroupBy(o => o.Type.Split('.').Last())
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => (g.Key, (double)g.Count()))
            .ToList();
        if (typeSegs.Count > 1)
            sink.DonutChart(typeSegs, "HTTP objects by type", $"{data.Objects.Count:N0}\ntotal");

        RenderRequestDetails(sink, data.Objects);
        RenderServicePoints(sink, data.ServicePoints);
        RenderResponseCodes(sink, data.Objects);
        RenderAsyncCorrelations(sink, data);
        if (showAddr) RenderAddresses(sink, data.Objects);
    }

    private static void RenderRequestDetails(IRenderSink sink, IReadOnlyList<HttpObjectEntry> objects)
    {
        var requests = objects.Where(o =>
            o.Type is "System.Net.Http.HttpRequestMessage" or "System.Net.HttpWebRequest").ToList();
        if (requests.Count == 0) return;

        sink.Section("In-Flight Requests");

        // Group by host, then list all full URIs under each host in a details block
        var byHost = requests
            .GroupBy(r => ExtractHost(r.Uri))
            .OrderByDescending(g => g.Count())
            .ToList();

        // Host-level summary table
        var hostRows = byHost.Select(g => new[]
        {
            g.Key,
            g.Count().ToString("N0"),
            string.Join(", ", g.Select(r => r.Method).Distinct().Take(5)),
        }).ToList();
        sink.Table(["Host", "Count", "Methods"], hostRows);

        // Per-host URI breakdown (collapsed by default)
        foreach (var hostGroup in byHost)
        {
            var uriRows = hostGroup
                .GroupBy(r => (r.Method, Uri: string.IsNullOrEmpty(r.Uri) ? "(no URI)" : r.Uri))
                .OrderByDescending(g => g.Count())
                .Select(g => new[] { g.Key.Method, g.Key.Uri, g.Count().ToString("N0") })
                .ToList();

            sink.BeginDetails($"{hostGroup.Key}  ({hostGroup.Count()} request(s))", open: false);
            sink.Table(["Method", "Full URI", "Count"], uriRows);
            sink.EndDetails();
        }
    }

    private static void RenderServicePoints(IRenderSink sink, IReadOnlyList<ServicePointEntry>? servicePoints)
    {
        if (servicePoints is null || servicePoints.Count == 0) return;

        sink.Section("Outbound Connection Pools (ServicePoint)");
        sink.Explain(
            what: "System.Net.ServicePoint objects represent active outbound HTTP connection pools — one per remote server endpoint. They persist even after request objects are GC'd.",
            why:  "ServicePoints show which external servers this process is currently connected to and how many simultaneous connections are open, regardless of whether any requests are in-flight.",
            impact: "High CurrentConnections relative to ConnectionLimit can cause request queuing and latency. Unexpected endpoints may reveal misconfiguration or data leaks.",
            bullets: [
                "ConnectionLimit default is 2 for most scenarios — increase via ServicePointManager.DefaultConnectionLimit for high-throughput services",
                "CurrentConnections > 0 at dump time means active I/O to that endpoint",
            ],
            action: "Review each endpoint — unexpected entries may indicate misconfigured clients. For known endpoints with high connections, increase ConnectionLimit."
        );

        int totalActive = servicePoints.Sum(sp => sp.CurrentConnections);
        sink.KeyValues([
            ("Unique remote endpoints", servicePoints.Count.ToString("N0")),
            ("Total active connections", totalActive.ToString("N0")),
        ]);

        var rows = servicePoints
            .OrderByDescending(sp => sp.CurrentConnections)
            .Select(sp => new[]
            {
                string.IsNullOrEmpty(sp.Address) ? $"{sp.Host}:{sp.Port}" : sp.Address,
                sp.CurrentConnections.ToString("N0"),
                sp.ConnectionLimit.ToString("N0"),
                sp.CurrentConnections >= sp.ConnectionLimit ? "AT LIMIT" : "",
            })
            .ToList();
        sink.Table(["Endpoint", "Active Connections", "Limit", "Status"], rows);

        var atLimit = servicePoints.Count(sp => sp.CurrentConnections >= sp.ConnectionLimit && sp.CurrentConnections > 0);
        if (atLimit > 0)
            sink.Alert(AlertLevel.Warning, $"{atLimit} endpoint(s) at connection limit.",
                "Requests to these endpoints will queue until a connection is freed.",
                "Raise ServicePointManager.DefaultConnectionLimit or use HttpClientFactory with a SocketsHttpHandler.");
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

        var catSegs = responses
            .GroupBy(o => StatusCategory(o.StatusCode))
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key, (double)g.Count()))
            .ToList();
        if (catSegs.Count > 1)
            sink.DonutChart(catSegs, "Responses by status category", $"{responses.Count:N0}\nresps");
    }

    private static void RenderAddresses(IRenderSink sink, IReadOnlyList<HttpObjectEntry> objects)
    {
        sink.Section("Object Addresses (up to 200)");
        var rows = objects.Take(200)
            .Select(o => new[] { o.Type, $"0x{o.Addr:X16}", DumpHelpers.FormatSize(o.Size), o.Method, o.Uri }).ToList();
        sink.Table(["Type", "Address", "Size", "Method", "URI"], rows);
    }

    private static void RenderAsyncCorrelations(IRenderSink sink, HttpRequestsData data)
    {
        var corrs = data.AsyncCorrelations;
        if (corrs is null || corrs.Count == 0) return;

        sink.Section("Async State Machines Likely Serving Requests");
        sink.Explain(
            what: "Async state machines on the heap whose method names suggest they are currently " +
                  "handling an HTTP request (names contain Controller, Handler, Endpoint, Middleware, etc.).",
            why:  "Correlating in-flight HTTP requests with suspended async state machines reveals how many " +
                  "concurrent requests are actively being processed — and where they are awaiting.",
            impact: "If the number of suspended async machines significantly exceeds the in-flight request count, " +
                    "it indicates cascading awaits, retry loops, or downstream I/O backpressure.",
            action: "Use 'async-stacks <dump>' for a full async backlog view. " +
                    "For request-level tracing, combine with 'http-trace' from an ETW trace file."
        );

        sink.KeyValues([
            ("Suspended HTTP-like state machines", corrs.Count.ToString("N0")),
            ("In-flight HTTP objects",             data.Objects.Count(o =>
                o.Type is "System.Net.Http.HttpRequestMessage" or "System.Net.HttpWebRequest").ToString("N0")),
        ]);

        if (corrs.Count > data.Objects.Count * 3)
            sink.Alert(AlertLevel.Warning,
                $"{corrs.Count} suspended async machines for {data.Objects.Count} HTTP objects.",
                "More async continuations than in-flight requests suggests cascading awaits or downstream backpressure.",
                "Profile with 'async-stacks' to identify the deepest await chains.");

        var rows = corrs
            .Take(100)
            .Select(c => new[] { c.StateMachineMethod, c.State, $"0x{c.Addr:X16}", c.CorrelationHint })
            .ToList();
        sink.Table(["Async Method", "State", "Address", "Request Context"], rows,
            $"{corrs.Count} suspended async method(s) with HTTP-handling signatures");
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
