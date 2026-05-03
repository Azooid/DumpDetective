using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Types;

public sealed class TypeInstancesScenario : IScenario
{
    private sealed class TargetObject(int Id, string Name, double Value)
    {
        public int    Id    { get; } = Id;
        public string Name  { get; } = Name;
        public double Value { get; } = Value;
    }

    private static readonly List<TargetObject> _instances = [];

    public string CommandName => "type-instances";
    public string Description => "500 TargetObject instances for type-instances command.";

    public void Setup()
    {
        for (int i = 0; i < 500; i++)
            _instances.Add(new TargetObject(i, $"target-{i:D4}", i * 1.5));
    }

    public void Validate(ReportDoc doc)
    {
        // Render() is called without --type; the command emits an informational alert
        // telling the caller to use Run() with --type instead of Render()
        DocAssert.AlertContains(doc, "type-instances requires --type");
    }
}
