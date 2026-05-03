using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Gc;

// 1001 WeakReference<WeakTarget> with live strong-ref targets.
// Test asserts: "WeakTarget" in alive object type table, alert fires (> 1000).

internal sealed class WeakTarget(int Id, string Label)
{
    public int    Id    = Id;
    public string Label = Label;
}

public sealed class WeakRefsScenario : IScenario
{
    private static readonly List<WeakReference<WeakTarget>> _refs    = [];
    private static readonly List<WeakTarget>                _targets = [];
    public string CommandName => "weak-refs";
    public string Description => "1001 WeakReference<T> objects with live targets.";

    public void Setup()
    {
        for (int i = 0; i < 1001; i++)
        {
            var t = new WeakTarget(i, $"weak-target-{i}");
            _targets.Add(t);
            _refs.Add(new WeakReference<WeakTarget>(t));
        }
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Weak Reference Summary");
        DocAssert.HasKeyValue(doc, "Total weak handles");
        DocAssert.AlertContains(doc, "weak handles");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "alive weak-ref types table",
            "Alive Object Type", "Count");
        DocAssert.AnyTableContainsText(doc, "WeakTarget",
            "our 1001 WeakTarget objects must be listed as alive referents");
    }
}
