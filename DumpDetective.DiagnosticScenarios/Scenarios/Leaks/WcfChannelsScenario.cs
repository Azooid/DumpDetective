using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Leaks;

// No stub ServiceModel types available; structural assertions only.

public sealed class WcfChannelsScenario : IScenario
{
    public string CommandName   => "wcf-channels";
    public string Description   => "No-setup: validates command runs cleanly when no WCF channels present.";
    public bool   SafeInProcess => true;
    public void   Setup() { }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Summary");
        DocAssert.HasContent(doc);
    }
}
