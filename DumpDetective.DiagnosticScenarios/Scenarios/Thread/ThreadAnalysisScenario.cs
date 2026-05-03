using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Thread;

// 20 named threads blocked on a gate.
// Test asserts: Details titles contain "DDTestWorker".

public sealed class ThreadAnalysisScenario : IScenario
{
    private static readonly ManualResetEventSlim _gate    = new(false);
    private static readonly List<System.Threading.Thread> _threads = [];
    public string CommandName => "thread-analysis";
    public string Description => "20 named background threads blocked on a wait gate.";

    public void Setup()
    {
        for (int i = 0; i < 20; i++)
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
        // Wait for all threads to actually block before we dump
        System.Threading.Thread.Sleep(100);
    }

    public void Teardown()
    {
        _gate.Set();
        foreach (var t in _threads)
            t.Join(TimeSpan.FromSeconds(2));
        _threads.Clear();
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Thread Summary");
        DocAssert.HasKeyValue(doc, "Total threads");
        DocAssert.HasKeyValue(doc, "Named threads");
        var namedStr = DocAssert.GetKeyValue(doc, "Named threads");
        Assert.True(
            namedStr is not null && long.TryParse(namedStr.Replace(",", ""), out long namedCount) && namedCount >= 20,
            $"Expected Named threads ≥ 20 (we planted 20). Got: '{namedStr}'");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "thread category table",
            "Category", "Count");
        DocAssert.AnyDetailsTitleContains(doc, "DDTestWorker",
            "our 20 planted named threads must appear in thread card accordion titles");
    }
}
