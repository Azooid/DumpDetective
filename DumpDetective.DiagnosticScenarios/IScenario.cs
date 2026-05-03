using DumpDetective.Core.Models;

namespace DumpDetective.DiagnosticScenarios;

public interface IScenario
{
    string CommandName  { get; }
    string Description  { get; }
    bool   SafeInProcess => true;
    void   Setup();
    void   Validate(ReportDoc doc) { }
    void   Teardown() { }
}
