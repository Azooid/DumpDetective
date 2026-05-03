using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Thread;

// NOT safe in-process (would block the ScenarioHost itself).
// Produces a dump of the idle process — structural assertions still pass.

public sealed class ThreadPoolScenario : IScenario
{
    public string CommandName   => "thread-pool";
    public string Description   => "Thread-pool starvation scenario (UNSAFE — setup skipped in-process).";
    public bool   SafeInProcess => false; // setup skipped
    public void   Setup() { }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Thread Pool State");
        DocAssert.HasContent(doc);
    }
}
