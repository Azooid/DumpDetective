using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Async;

public sealed class HttpRequestsScenario : IScenario
{
    private static readonly List<HttpClient>         _clients  = [];
    private static readonly List<HttpRequestMessage> _requests = [];

    public string CommandName => "http-requests";
    public string Description => "15 leaked HttpClient instances + 20 stalled HttpRequestMessage objects.";

    public void Setup()
    {
        for (int i = 0; i < 15; i++)
            _clients.Add(new HttpClient { BaseAddress = new Uri("https://dummyjson.com") });

        for (int i = 0; i < 20; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://dummyjson.com/posts/{i}");
            req.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString());
            _requests.Add(req);
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always rendered
        DocAssert.HasSection(doc, "Summary");

        // Object summary table: [Type, Count, Size]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "http object summary",
            "Type", "Count", "Size");

        // We planted 15 HttpClient instances — must appear in the table
        DocAssert.AnyTableContainsText(doc, "HttpClient",
            "our 15 planted HttpClient instances must be detected");

        // We planted 20 HttpRequestMessage objects
        DocAssert.AnyTableContainsText(doc, "HttpRequestMessage",
            "our 20 planted HttpRequestMessage objects must be detected");

        // URIs must be captured now that we navigate Uri._string
        // The "In-Flight Requests" section appears when requests with URIs exist
        DocAssert.HasSection(doc, "In-Flight Requests");
        // Host-level summary table: [Host, Count, Methods]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "in-flight requests host summary table",
            "Host", "Count", "Methods");
        DocAssert.AnyTableContainsText(doc, "dummyjson.com",
            "dummyjson.com must appear in the host summary (and in per-host URI detail tables)");
    }

    public void Teardown()
    {
        foreach (var c in _clients)  c.Dispose();
        foreach (var r in _requests) r.Dispose();
        _clients.Clear();
        _requests.Clear();
    }
}
