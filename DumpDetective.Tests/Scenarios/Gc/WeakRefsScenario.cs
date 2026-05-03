using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Gc;

public sealed class WeakRefsScenario : IScenario
{
    private sealed class WeakTarget(int Id, string Label)
    {
        public int Id { get; } = Id;
        public string Label { get; } = Label;
    }

    private static readonly List<WeakReference<WeakTarget>> _refs = [];
    // Strong list keeps targets alive so weak refs are not collected before the dump
    private static readonly List<WeakTarget> _targets = [];

    public string CommandName => "weak-refs";
    public string Description => "1001 WeakReference<T> objects with live targets.";

    public void Setup()
    {
        for (int i = 0; i < 1001; i++)
        {
            var target = new WeakTarget(i, $"weak-target-{i}");
            _targets.Add(target);
            _refs.Add(new WeakReference<WeakTarget>(target));
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always present
        DocAssert.HasSection(doc, "Weak Reference Summary");

        // KV summary: "Total weak handles" must report the count
        DocAssert.HasKeyValue(doc, "Total weak handles");

        // Alert fires when total > 1000 (we planted 1001)
        DocAssert.AlertContains(doc, "weak handles");

        // Alive object types table: [Alive Object Type, Count]
        // We have 1001 live WeakReference<WeakTarget> with live targets
        DocAssert.TableByHeadersHasMinRows(doc, 1, "alive weak-ref types table",
            "Alive Object Type", "Count");

        // WeakTarget must appear as an alive object type in the table
        DocAssert.AnyTableContainsText(doc, "WeakTarget",
            "our 1001 WeakTarget objects must be listed as alive referents");
    }
}
