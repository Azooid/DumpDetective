using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Types;

// 500 TargetObject instances.  Command run without --type → emits "requires --type" alert.

internal sealed class TargetObject(int Id, string Name, double Value)
{
    public int    Id    = Id;
    public string Name  = Name;
    public double Value = Value;
}

public sealed class TypeInstancesScenario : IScenario
{
    private static readonly List<TargetObject> _instances = [];
    public string CommandName => "type-instances";
    public string Description => "500 TargetObject instances for type-instances command.";

    public void Setup()
    {
        for (int i = 0; i < 500; i++)
            _instances.Add(new TargetObject(i, $"target-{i:D4}", i * 1.5));
    }

    public void Validate(ReportDoc doc) =>
        DocAssert.AlertContains(doc, "type-instances requires --type");
}
