using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Thread;

// NOT safe in-process.  Produces a dump with no deadlock — structural assertions
// (Analysis Summary section + no Deadlock Cycles section) still pass.

public sealed class DeadlockScenario : IScenario
{
    public string CommandName   => "deadlock-detection";
    public string Description   => "Deadlock scenario (UNSAFE — setup skipped in-process).";
    public bool   SafeInProcess => false; // setup skipped
    public void   Setup() { }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Analysis Summary");
        var sections = DocAssert.AllSections(doc).Select(s => s.Title ?? "").ToList();
        Assert.False(
            sections.Any(t => t.Contains("Deadlock Cycles", StringComparison.OrdinalIgnoreCase)),
            $"No deadlock was planted, but a 'Deadlock Cycles' section was found: {string.Join(", ", sections)}");
    }
}
