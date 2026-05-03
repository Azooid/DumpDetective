using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Leaks;

/// <summary>
/// The wcf-channels command looks for <c>System.ServiceModel.*</c> types which are
/// only present via the DiagnosticScenarios stubs (not referenced here).
/// Validates the command runs cleanly on a dump without WCF objects.
/// </summary>
public sealed class WcfChannelsScenario : IScenario
{
    public string CommandName => "wcf-channels";
    public string Description => "No-setup: validates command runs cleanly when no WCF channels present.";

    public void Setup() { /* no ServiceModel stubs available in test project */ }

    public void Validate(ReportDoc doc)
    {
        // Summary section is always emitted — even when no WCF objects are present
        DocAssert.HasSection(doc, "Summary");

        // No System.ServiceModel stubs available — command must complete without crashing
        DocAssert.HasContent(doc);
    }
}
