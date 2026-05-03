using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Thread;

/// <summary>
/// NOT safe to run in-process — creating a real deadlock would hang the test runner.
/// Command is still exercised against the dump (with no deadlock data) to verify
/// it produces a valid document.
/// </summary>
public sealed class DeadlockScenario : IScenario
{
    public string CommandName  => "deadlock-detection";
    public string Description  => "Deadlock scenario (UNSAFE — setup skipped in-process).";
    public bool   SafeInProcess => false;

    public void Setup()  { /* never called — SafeInProcess = false */ }

    public void Validate(ReportDoc doc)
    {
        // Analysis summary is always emitted
        DocAssert.HasSection(doc, "Analysis Summary");

        // We planted no deadlock — verify no confirmed cycles were reported.
        // The "Deadlock Cycles" section only appears when cycles are confirmed.
        var sections = DocAssert.AllSections(doc).Select(s => s.Title ?? "").ToList();
        Assert.False(
            sections.Any(t => t.Contains("Deadlock Cycles", StringComparison.OrdinalIgnoreCase)),
            $"No deadlock was planted, but a 'Deadlock Cycles' section was found: {string.Join(", ", sections)}");
    }
}
