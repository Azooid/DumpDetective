using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Leaks;

/// <summary>
/// The connection-pool command detects <c>System.Data.SqlClient.SqlConnection</c>
/// objects whose exact CLR type name is only produced by the stub types in
/// DumpDetective.DiagnosticScenarios (not referenced here).
/// This scenario validates that the command runs cleanly against a dump that
/// has no such objects, producing an empty-but-valid document.
/// </summary>
public sealed class ConnectionPoolScenario : IScenario
{
    public string CommandName => "connection-pool";
    public string Description => "No-setup: validates command runs cleanly when no connections present.";

    public void Setup() { /* no SqlConnection stubs available in test project */ }

    public void Validate(ReportDoc doc)
    {
        // ConnectionPoolReport returns BEFORE creating any named section when no connections
        // are found (it emits a Text element in the auto-section instead).
        // Validate the command completed without crashing and produced output.
        DocAssert.HasContent(doc);
    }
}
