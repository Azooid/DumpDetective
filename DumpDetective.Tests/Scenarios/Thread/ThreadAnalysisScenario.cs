using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Thread;

public sealed class ThreadAnalysisScenario : IScenario
{
    private static readonly ManualResetEventSlim _gate = new(false);
    private static readonly List<System.Threading.Thread> _threads = [];

    public string CommandName => "thread-analysis";
    public string Description => "20 named background threads blocked on a wait gate.";

    public void Setup()
    {
        const int count = 20;
        for (int i = 0; i < count; i++)
        {
            int id = i;
            var t = new System.Threading.Thread(() => _gate.Wait())
            {
                Name         = $"DDTestWorker-{id:D2}",
                IsBackground = true,
            };
            _threads.Add(t);
            t.Start();
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Thread summary section is always emitted
        DocAssert.HasSection(doc, "Thread Summary");

        // KV summary must include thread count metrics
        DocAssert.HasKeyValue(doc, "Total threads");
        DocAssert.HasKeyValue(doc, "Named threads");

        // We started 20 named threads — named count must be ≥ 20
        var namedStr = DocAssert.GetKeyValue(doc, "Named threads");
        Assert.True(
            namedStr is not null && long.TryParse(namedStr.Replace(",", ""), out long namedCount) && namedCount >= 20,
            $"Expected Named threads ≥ 20 (we planted 20). Got: '{namedStr}'");

        // Category breakdown table: [Category, Count]
        DocAssert.TableByHeadersHasMinRows(doc, 1, "thread category table",
            "Category", "Count");

        // Thread cards mode is used (showStacks=true in Render).
        // Each DDTestWorker thread has its name embedded in the Details accordion title.
        DocAssert.AnyDetailsTitleContains(doc, "DDTestWorker",
            "our 20 planted named threads must appear in thread card accordion titles");
    }

    public void Teardown()
    {
        _gate.Set(); // unblock all DiagWorker threads
        foreach (var t in _threads)
            t.Join(TimeSpan.FromSeconds(2));
        _threads.Clear();
    }
}
