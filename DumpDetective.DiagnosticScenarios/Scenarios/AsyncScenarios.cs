namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: async-stacks, http-requests
internal static class AsyncScenarios
{
    // ── async-stacks ──────────────────────────────────────────────────────────
    // 100 async methods each await a TaskCompletionSource that is never completed.
    // Their state machines are kept alive on the managed heap as objects whose
    // type names end in "<SuspendedWorker>d__N", visible to DumpDetective async-stacks.
    private static readonly TaskCompletionSource _neverCompletes = new();
    private static readonly List<Task> _suspendedTasks = [];

    public static IResult TriggerAsyncStacks()
    {
        const int count = 100;
        for (int i = 0; i < count; i++)
            _suspendedTasks.Add(SuspendedWorker(i, $"job-{i:D3}"));

        return Results.Ok(new
        {
            message = $"{count} async state machines suspended on heap.",
            command = "DumpDetective async-stacks <dump.dmp>",
            hint = "Look for '<SuspendedWorker>d__' types in the output.",
        });
    }

    // Each invocation creates one compiler-generated IAsyncStateMachine instance
    // that stays allocated until _neverCompletes is resolved.
    private static async Task SuspendedWorker(int id, string label)
    {
        await Task.Yield(); // ensure continuation runs on pool, not inline
        await _neverCompletes.Task.ConfigureAwait(false);
        // The lines below never execute — they just give the state machine fields
        GC.KeepAlive(id);
        GC.KeepAlive(label);
    }

    public static string AsyncStatus => $"async-stacks: {_suspendedTasks.Count} suspended tasks";

    // ── http-requests ─────────────────────────────────────────────────────────
    // Creates leaked HttpClient instances and long-lived HttpRequestMessage objects.
    // DumpDetective http-requests looks for System.Net.Http.HttpClient,
    // System.Net.Http.HttpRequestMessage, and System.Net.Http.HttpClientHandler
    // on the heap.
    private static readonly List<HttpClient> _leakedClients = [];
    private static readonly List<HttpRequestMessage> _stalledRequests = [];
    private static readonly TaskCompletionSource<HttpResponseMessage> _stallGate = new();

    public static IResult TriggerHttpRequests()
    {
        // Leaked HttpClient instances (should be singleton / IHttpClientFactory)
        for (int i = 0; i < 20; i++)
            _leakedClients.Add(new HttpClient { BaseAddress = new Uri("https://api.contoso.com") });

        // HttpRequestMessage objects that are "in flight" but never complete
        for (int i = 0; i < 30; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.contoso.com/orders/{i}");
            req.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
            _stalledRequests.Add(req);
        }

        return Results.Ok(new
        {
            message = $"{_leakedClients.Count} leaked HttpClient instances, {_stalledRequests.Count} stalled HttpRequestMessage objects.",
            command = "DumpDetective http-requests <dump.dmp>",
        });
    }

    public static string HttpStatus => $"http-requests: {_leakedClients.Count} clients, {_stalledRequests.Count} requests";

    public static void Reset()
    {
        foreach (var c in _leakedClients) c.Dispose();
        _leakedClients.Clear();
        foreach (var r in _stalledRequests) r.Dispose();
        _stalledRequests.Clear();
        // _neverCompletes is intentionally never resolved (process restart required to clear suspended tasks)
    }
}
