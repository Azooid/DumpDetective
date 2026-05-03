using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Async;

// 15 leaked HttpClient instances + 20 stalled HttpRequestMessage objects.
// Test asserts: "dummyjson.com", "HttpClient", "HttpRequestMessage".

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
        DocAssert.HasSection(doc, "Summary");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "http object summary",
            "Type", "Count", "Size");
        DocAssert.AnyTableContainsText(doc, "HttpClient",
            "our 15 planted HttpClient instances must be detected");
        DocAssert.AnyTableContainsText(doc, "HttpRequestMessage",
            "our 20 planted HttpRequestMessage objects must be detected");
        DocAssert.HasSection(doc, "In-Flight Requests");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "in-flight requests host summary table",
            "Host", "Count", "Methods");
        DocAssert.AnyTableContainsText(doc, "dummyjson.com",
            "dummyjson.com must appear in the host summary");
    }

    public void Teardown()
    {
        foreach (var c in _clients)  c.Dispose();
        foreach (var r in _requests) r.Dispose();
        _clients.Clear();
        _requests.Clear();
    }
}
