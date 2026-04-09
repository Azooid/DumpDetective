using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class HttpRequestsCommand
{
    private const string Help = """
        Usage: DumpDetective http-requests <dump-file> [options]

        Options:
          -a, --addresses    Show object addresses (up to 200)
          -o, --output <f>   Write report to file (.md / .html / .txt)
          -h, --help         Show this help
        """;

    // Exact type names for O(1) lookup
    private static readonly HashSet<string> HttpTypeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Net.Http.HttpRequestMessage",
        "System.Net.Http.HttpResponseMessage",
        "System.Net.HttpWebRequest",
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpClientHandler",
        "System.Net.Http.SocketsHttpHandler",
    };

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — HTTP Objects",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var found = new List<(string Type, ulong Addr, long Size, string Method, string Uri, int StatusCode)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning HTTP objects...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!HttpTypeSet.Contains(name)) continue;

                long   size       = (long)obj.Size;
                string method     = "";
                string uri        = "";
                int    statusCode = 0;

                try
                {
                    // HttpRequestMessage: _requestUri (Uri) → _string, _method → _method string
                    if (name == "System.Net.Http.HttpRequestMessage")
                    {
                        uri    = ReadUri(obj);
                        method = ReadHttpMethod(obj);
                    }
                    // HttpResponseMessage: _statusCode int
                    else if (name == "System.Net.Http.HttpResponseMessage")
                    {
                        try { statusCode = obj.ReadField<int>("_statusCode"); } catch { }
                        // also try to get the request URI from _requestMessage
                        try
                        {
                            var req = obj.ReadObjectField("_requestMessage");
                            if (!req.IsNull && req.IsValid) uri = ReadUri(req);
                        }
                        catch { }
                    }
                }
                catch { }

                found.Add((name, obj.Address, size, method, uri, statusCode));
            }
        });

        sink.Section("Summary");
        if (found.Count == 0) { sink.Text("No HTTP objects found."); return; }

        var summary = found
            .GroupBy(f => f.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(f => f.Size)),
            })
            .ToList();
        sink.Table(["Type", "Count", "Size"], summary);
        sink.KeyValues([("Total HTTP objects", found.Count.ToString("N0"))]);

        // ── HttpClient leak alert ─────────────────────────────────────────────
        int clientCount = found.Count(f =>
            f.Type is "System.Net.Http.HttpClient" or
                      "System.Net.Http.HttpClientHandler" or
                      "System.Net.Http.SocketsHttpHandler");

        if (clientCount > 1)
            sink.Alert(AlertLevel.Critical,
                $"{clientCount} HttpClient/Handler instance(s) found on heap — likely leak.",
                advice: "HttpClient should be a long-lived singleton (or use IHttpClientFactory). " +
                        "Creating per-request instances exhausts socket resources and causes DNS refresh failures.");
        else if (clientCount == 1)
            sink.Alert(AlertLevel.Info, "1 HttpClient/Handler instance found — verify it is a singleton.");

        // ── Request details ───────────────────────────────────────────────────
        var requests = found.Where(f =>
            f.Type is "System.Net.Http.HttpRequestMessage" or "System.Net.HttpWebRequest").ToList();

        if (requests.Count > 0)
        {
            sink.Section("In-Flight Requests");
            var methodGroups = requests
                .Where(r => r.Method.Length > 0)
                .GroupBy(r => r.Method)
                .Select(g => new[] { g.Key, g.Count().ToString("N0") })
                .ToList();
            if (methodGroups.Count > 0)
                sink.Table(["HTTP Method", "Count"], methodGroups);

            var reqRows = requests
                .Where(r => r.Uri.Length > 0)
                .Take(50)
                .Select(r => new[]
                {
                    r.Method.Length > 0 ? r.Method : "?",
                    r.Uri,
                    DumpHelpers.FormatSize(r.Size),
                })
                .ToList();
            if (reqRows.Count > 0)
                sink.Table(["Method", "URI", "Size"], reqRows, $"First {reqRows.Count} requests with URI");
        }

        // ── Response status code distribution ─────────────────────────────────
        var responses = found.Where(f =>
            f.Type == "System.Net.Http.HttpResponseMessage" && f.StatusCode > 0).ToList();
        if (responses.Count > 0)
        {
            sink.Section("Response Status Codes");
            var codeRows = responses
                .GroupBy(r => r.StatusCode)
                .OrderByDescending(g => g.Count())
                .Select(g => new[] { g.Key.ToString(), HttpStatusLabel(g.Key), g.Count().ToString("N0") })
                .ToList();
            sink.Table(["Status Code", "Meaning", "Count"], codeRows);
        }

        if (showAddr)
        {
            sink.Section("Object Addresses");
            var addrRows = found.Take(200).Select(f => new[]
            {
                $"0x{f.Addr:X16}", f.Type, f.Method.Length > 0 ? f.Method : "—",
                f.Uri.Length > 0 ? f.Uri : "—", DumpHelpers.FormatSize(f.Size),
            }).ToList();
            sink.Table(["Address", "Type", "Method", "URI", "Size"], addrRows);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string ReadUri(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        // _requestUri is a System.Uri object — its string is in _string
        try
        {
            var uriObj = obj.ReadObjectField("_requestUri");
            if (!uriObj.IsNull && uriObj.IsValid)
            {
                // Try the Uri._string backing field
                try { return uriObj.ReadStringField("_string") ?? ""; } catch { }
                // Fallback: AbsoluteUri via _info._moreInfo._absoluteUri
                try
                {
                    var info = uriObj.ReadObjectField("_info");
                    if (!info.IsNull && info.IsValid)
                    {
                        var moreInfo = info.ReadObjectField("_moreInfo");
                        if (!moreInfo.IsNull && moreInfo.IsValid)
                            return moreInfo.ReadStringField("_absoluteUri") ?? "";
                    }
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    static string ReadHttpMethod(Microsoft.Diagnostics.Runtime.ClrObject obj)
    {
        // HttpMethod is an object with _method string field
        try
        {
            var methodObj = obj.ReadObjectField("_method");
            if (!methodObj.IsNull && methodObj.IsValid)
                return methodObj.ReadStringField("_method") ?? "";
        }
        catch { }
        return "";
    }

    static string HttpStatusLabel(int code) => code switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content",
        301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 408 => "Request Timeout", 409 => "Conflict",
        429 => "Too Many Requests",
        500 => "Internal Server Error", 502 => "Bad Gateway",
        503 => "Service Unavailable", 504 => "Gateway Timeout",
        >= 100 and < 200 => "1xx Informational",
        >= 200 and < 300 => "2xx Success",
        >= 300 and < 400 => "3xx Redirect",
        >= 400 and < 500 => "4xx Client Error",
        >= 500           => "5xx Server Error",
        _                => "",
    };
}
