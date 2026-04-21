using DumpDetective.DiagnosticScenarios.Scenarios;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Landing page ──────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Content(LandingPage.Build(), "text/html"));

// ── Heap & Memory ─────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/heap-stats",         HeapScenarios.TriggerHeapStats);
app.MapGet("/api/diagscenario/gen-summary",         HeapScenarios.TriggerGenSummary);
app.MapGet("/api/diagscenario/large-objects",       HeapScenarios.TriggerLargeObjects);
app.MapGet("/api/diagscenario/memory-leak",         HeapScenarios.TriggerMemoryLeak);

// ── GC / Handles ──────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/high-refs",           GcScenarios.TriggerHighRefs);
app.MapGet("/api/diagscenario/heap-fragmentation",  GcScenarios.TriggerHeapFragmentation);
app.MapGet("/api/diagscenario/pinned-objects",      GcScenarios.TriggerPinnedObjects);
app.MapGet("/api/diagscenario/gc-roots",            GcScenarios.TriggerGcRoots);
app.MapGet("/api/diagscenario/finalizer-queue",     GcScenarios.TriggerFinalizerQueue);
app.MapGet("/api/diagscenario/handle-table",        GcScenarios.TriggerHandleTable);
app.MapGet("/api/diagscenario/static-refs",         GcScenarios.TriggerStaticRefs);
app.MapGet("/api/diagscenario/weak-refs",           GcScenarios.TriggerWeakRefs);

// ── Strings ───────────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/string-duplicates",   StringScenarios.TriggerStringDuplicates);

// ── Threads ───────────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/thread-analysis",      ThreadScenarios.TriggerThreadAnalysis);
app.MapGet("/api/diagscenario/thread-pool",          ThreadScenarios.TriggerThreadPool);
app.MapGet("/api/diagscenario/thread-pool-starvation", ThreadScenarios.TriggerThreadPoolStarvation);
app.MapGet("/api/diagscenario/deadlock-detection",   ThreadScenarios.TriggerDeadlock);

// ── Async ─────────────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/async-stacks",         AsyncScenarios.TriggerAsyncStacks);
app.MapGet("/api/diagscenario/http-requests",        AsyncScenarios.TriggerHttpRequests);

// ── Exceptions & Events ───────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/exception-analysis",  ExceptionScenarios.TriggerExceptionAnalysis);
app.MapGet("/api/diagscenario/event-analysis",       EventScenarios.TriggerEventAnalysis);

// ── Connections & Timers ──────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/connection-pool",      ConnectionScenarios.TriggerConnectionPool);
app.MapGet("/api/diagscenario/wcf-channels",         ConnectionScenarios.TriggerWcfChannels);
app.MapGet("/api/diagscenario/timer-leaks",          TimerScenarios.TriggerTimerLeaks);

// ── Types ─────────────────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/type-instances",       TypeScenarios.TriggerTypeInstances);
app.MapGet("/api/diagscenario/object-inspect",       TypeScenarios.TriggerObjectInspect);

// ── Informational (no heap artefact needed) ───────────────────────────────────
app.MapGet("/api/diagscenario/module-list",
    () => Results.Ok(new { message = "All loaded modules are visible in any dump. No extra setup needed.", hint = "Run: DumpDetective module-list <dump.dmp>" }));
app.MapGet("/api/diagscenario/trend-analysis",
    () => Results.Ok(new { message = "Take 2+ dumps minutes apart, then: DumpDetective trend-analysis <dir>" }));
app.MapGet("/api/diagscenario/trend-render",
    () => Results.Ok(new { message = "After trend-analysis saved .json files: DumpDetective trend-render <dir>" }));
app.MapGet("/api/diagscenario/render",
    () => Results.Ok(new { message = "DumpDetective render re-renders a saved .json report: DumpDetective render <report.json>" }));

// ── Status & Control ──────────────────────────────────────────────────────────
app.MapGet("/api/diagscenario/status", () => Results.Ok(new
{
    heapStats        = HeapScenarios.Status,
    genSummary       = HeapScenarios.GenSummaryStatus,
    largeObjects     = HeapScenarios.LargeObjectsStatus,
    memoryLeak       = HeapScenarios.MemoryLeakStatus,
    highRefs         = GcScenarios.HighRefsStatus,
    heapFragmentation = GcScenarios.FragmentationStatus,
    pinnedObjects    = GcScenarios.PinnedStatus,
    gcRoots          = GcScenarios.RootsStatus,
    finalizerQueue   = GcScenarios.FinalizerStatus,
    handleTable      = GcScenarios.HandleStatus,
    staticRefs       = GcScenarios.StaticStatus,
    weakRefs         = GcScenarios.WeakStatus,
    stringDuplicates = StringScenarios.Status,
    threadAnalysis   = ThreadScenarios.ThreadStatus,
    threadPool       = ThreadScenarios.PoolStatus,
    threadPoolStarvation = ThreadScenarios.StarvationStatus,
    deadlock         = ThreadScenarios.DeadlockStatus,
    asyncStacks      = AsyncScenarios.AsyncStatus,
    httpRequests     = AsyncScenarios.HttpStatus,
    exceptions       = ExceptionScenarios.Status,
    eventAnalysis    = EventScenarios.Status,
    connectionPool   = ConnectionScenarios.ConnectionStatus,
    wcfChannels      = ConnectionScenarios.WcfStatus,
    timerLeaks       = TimerScenarios.Status,
    typeInstances    = TypeScenarios.TypeStatus,
    objectInspect    = TypeScenarios.InspectStatus,
}));

app.MapPost("/api/diagscenario/reset", () =>
{
    HeapScenarios.Reset();
    GcScenarios.Reset();
    StringScenarios.Reset();
    ThreadScenarios.Reset();
    AsyncScenarios.Reset();
    ExceptionScenarios.Reset();
    EventScenarios.Reset();
    ConnectionScenarios.Reset();
    TimerScenarios.Reset();
    TypeScenarios.Reset();
    return Results.Ok(new { message = "All resettable scenarios cleared." });
});

app.Run();

// ── Landing page ──────────────────────────────────────────────────────────────
static class LandingPage
{
    public static string Build() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>DumpDetective — Diagnostic Scenarios</title>
          <style>
            body { font-family: Segoe UI, Arial, sans-serif; max-width: 900px; margin: 2rem auto; color: #222; }
            h1   { color: #0078d4; }
            h2   { margin-top: 2rem; border-bottom: 1px solid #ccc; padding-bottom: 4px; }
            table { border-collapse: collapse; width: 100%; }
            th, td { border: 1px solid #ddd; padding: 6px 10px; text-align: left; }
            th { background: #f3f3f3; }
            a  { color: #0078d4; }
            code { background: #f4f4f4; padding: 1px 4px; border-radius: 3px; font-size: .9em; }
            .hint { color: #555; font-size:.85em; }
          </style>
        </head>
        <body>
          <h1>🔍 DumpDetective — Diagnostic Scenarios</h1>
          <p>
            This service deliberately creates problematic heap and threading conditions.
            Take a Windows memory dump (<code>dotnet-dump collect -p &lt;pid&gt;</code>) while a scenario is active,
            then run the matching <strong>DumpDetective</strong> command against it.
          </p>
          <p>
            <a href="/api/diagscenario/status">📊 /api/diagscenario/status</a> &nbsp;|&nbsp;
            <a href="/api/diagscenario/status">POST /api/diagscenario/reset</a>
          </p>

          <h2>Heap &amp; Memory</h2>
          <table>
            <tr><th>Endpoint</th><th>DumpDetective command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/heap-stats">/api/diagscenario/heap-stats</a></td><td><code>heap-stats</code></td><td>10 custom types × 500 instances each on the managed heap</td></tr>
            <tr><td><a href="/api/diagscenario/gen-summary">/api/diagscenario/gen-summary</a></td><td><code>gen-summary</code></td><td>Objects promoted into Gen0/1/2 and LOH</td></tr>
            <tr><td><a href="/api/diagscenario/large-objects">/api/diagscenario/large-objects</a></td><td><code>large-objects</code></td><td>50 × 200 KB byte arrays on the LOH</td></tr>
            <tr><td><a href="/api/diagscenario/memory-leak">/api/diagscenario/memory-leak</a></td><td><code>memory-leak</code></td><td>Appends 1 MB to a static list on each call — never freed</td></tr>
          </table>

          <h2>GC / Handles</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/high-refs">/api/diagscenario/high-refs</a></td><td><code>high-refs</code></td><td>Hub object with 5 000 inbound references from spoke objects</td></tr>
            <tr><td><a href="/api/diagscenario/heap-fragmentation">/api/diagscenario/heap-fragmentation</a></td><td><code>heap-fragmentation</code></td><td>Alternating pinned/freed arrays leaving LOH gaps</td></tr>
            <tr><td><a href="/api/diagscenario/pinned-objects">/api/diagscenario/pinned-objects</a></td><td><code>pinned-objects</code></td><td>200 GCHandle.Pinned handles preventing compaction</td></tr>
            <tr><td><a href="/api/diagscenario/gc-roots">/api/diagscenario/gc-roots</a></td><td><code>gc-roots</code></td><td>Objects rooted via static fields and GCHandle.Normal</td></tr>
            <tr><td><a href="/api/diagscenario/finalizer-queue">/api/diagscenario/finalizer-queue</a></td><td><code>finalizer-queue</code></td><td>500 finalizable objects + blocked finalizer thread</td></tr>
            <tr><td><a href="/api/diagscenario/handle-table">/api/diagscenario/handle-table</a></td><td><code>handle-table</code></td><td>300 GCHandles of mixed types (Normal, Weak, Pinned)</td></tr>
            <tr><td><a href="/api/diagscenario/static-refs">/api/diagscenario/static-refs</a></td><td><code>static-refs</code></td><td>Large object graph anchored to static fields</td></tr>
            <tr><td><a href="/api/diagscenario/weak-refs">/api/diagscenario/weak-refs</a></td><td><code>weak-refs</code></td><td>1 000 WeakReference&lt;T&gt; instances</td></tr>
          </table>

          <h2>Strings</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/string-duplicates">/api/diagscenario/string-duplicates</a></td><td><code>string-duplicates</code></td><td>3 000 uninterned copies of the same strings</td></tr>
          </table>

          <h2>Threads</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/thread-analysis">/api/diagscenario/thread-analysis</a></td><td><code>thread-analysis</code></td><td>20 named threads each blocked on a wait handle</td></tr>
            <tr><td><a href="/api/diagscenario/thread-pool">/api/diagscenario/thread-pool</a></td><td><code>thread-pool</code></td><td>80 queued thread-pool work items sleeping</td></tr>
            <tr><td><a href="/api/diagscenario/thread-pool-starvation">/api/diagscenario/thread-pool-starvation</a></td><td><code>thread-pool-starvation</code></td><td>Sync-over-async blocking that saturates the thread pool</td></tr>
            <tr><td><a href="/api/diagscenario/deadlock-detection">/api/diagscenario/deadlock-detection</a></td><td><code>deadlock-detection</code></td><td>Classic two-lock deadlock on two named background threads</td></tr>
          </table>

          <h2>Async &amp; HTTP</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/async-stacks">/api/diagscenario/async-stacks</a></td><td><code>async-stacks</code></td><td>100 suspended async state machines awaiting a never-completing task</td></tr>
            <tr><td><a href="/api/diagscenario/http-requests">/api/diagscenario/http-requests</a></td><td><code>http-requests</code></td><td>Leaked HttpClient instances + stalled HttpRequestMessage objects</td></tr>
          </table>

          <h2>Exceptions &amp; Events</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/exception-analysis">/api/diagscenario/exception-analysis</a></td><td><code>exception-analysis</code></td><td>200 Exception instances of 5 types held in a static list</td></tr>
            <tr><td><a href="/api/diagscenario/event-analysis">/api/diagscenario/event-analysis</a></td><td><code>event-analysis</code></td><td>Static event publisher with 500 anonymous lambda subscribers never unsubscribed</td></tr>
          </table>

          <h2>Connections, WCF &amp; Timers</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/connection-pool">/api/diagscenario/connection-pool</a></td><td><code>connection-pool</code></td><td>100 undisposed System.Data.SqlClient.SqlConnection objects</td></tr>
            <tr><td><a href="/api/diagscenario/wcf-channels">/api/diagscenario/wcf-channels</a></td><td><code>wcf-channels</code></td><td>50 System.ServiceModel channel objects in Opened/Faulted states</td></tr>
            <tr><td><a href="/api/diagscenario/timer-leaks">/api/diagscenario/timer-leaks</a></td><td><code>timer-leaks</code></td><td>300 System.Threading.Timer instances never disposed</td></tr>
          </table>

          <h2>Type &amp; Object Inspection</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>What it creates</th></tr>
            <tr><td><a href="/api/diagscenario/type-instances">/api/diagscenario/type-instances</a></td><td><code>type-instances --type TargetObject</code></td><td>1 000 instances of DiagnosticScenarios.Scenarios.TargetObject</td></tr>
            <tr><td><a href="/api/diagscenario/object-inspect">/api/diagscenario/object-inspect</a></td><td><code>object-inspect --address &lt;addr&gt;</code></td><td>One well-known object — address returned in the response</td></tr>
          </table>

          <h2>Informational</h2>
          <table>
            <tr><th>Endpoint</th><th>Command</th><th>Notes</th></tr>
            <tr><td><a href="/api/diagscenario/module-list">/api/diagscenario/module-list</a></td><td><code>module-list</code></td><td>All loaded assemblies are visible in any dump automatically</td></tr>
            <tr><td><a href="/api/diagscenario/trend-analysis">/api/diagscenario/trend-analysis</a></td><td><code>trend-analysis</code></td><td>Take 2+ dumps, pass the folder to trend-analysis</td></tr>
            <tr><td><a href="/api/diagscenario/trend-render">/api/diagscenario/trend-render</a></td><td><code>trend-render</code></td><td>Replays previously saved trend .json files</td></tr>
            <tr><td><a href="/api/diagscenario/render">/api/diagscenario/render</a></td><td><code>render</code></td><td>Replays a saved .json report in any output format</td></tr>
          </table>
        </body>
        </html>
        """;
}
