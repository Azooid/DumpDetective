using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Leaks;

// No stub SqlConnection types available; structural assertions only.

public sealed class ConnectionPoolScenario : IScenario
{
    public string CommandName   => "connection-pool";
    public string Description   => "No-setup: validates command runs cleanly when no connections present.";
    public bool   SafeInProcess => true;
    public void   Setup() { }

    public void Validate(ReportDoc doc) => DocAssert.HasContent(doc);
}
