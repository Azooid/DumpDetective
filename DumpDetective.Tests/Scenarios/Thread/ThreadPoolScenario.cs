using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Thread;

/// <summary>
/// NOT safe to run in-process — queuing 80 blocking work items would stall the
/// xUnit runner's own thread pool and hang the test run.
/// The command still executes (finding empty data) and the doc is validated for
/// structural correctness only.
/// </summary>
public sealed class ThreadPoolScenario : IScenario
{
    public string CommandName  => "thread-pool";
    public string Description  => "Thread-pool starvation scenario (UNSAFE — setup skipped in-process).";
    public bool   SafeInProcess => false;

    public void Setup()  { /* never called — SafeInProcess = false */ }

    public void Validate(ReportDoc doc)
    {
        // Thread pool report always emits its state section
        DocAssert.HasSection(doc, "Thread Pool State");

        // Thread pool info may not be available in all dumps
        // (data.InfoAvailable = false → no KV or tables, just an alert).
        // Either path produces a valid document.
        DocAssert.HasContent(doc);
    }
}
