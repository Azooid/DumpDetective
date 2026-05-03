using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Async;

// 101 async state machines suspended on a TCS.
// Test asserts: "SuspendedWorker" in type table, suspended count ≥ 101.

public sealed class AsyncStacksScenario : IScenario
{
    private static readonly TaskCompletionSource _neverCompletes = new();
    private static readonly List<Task>           _tasks          = [];
    public string CommandName => "async-stacks";
    public string Description => "101 async state machines suspended on a TaskCompletionSource.";

    public void Setup()
    {
        for (int i = 0; i < 101; i++)
            _tasks.Add(SuspendedWorker(i));
    }

    private static async Task SuspendedWorker(int id)
    {
        await Task.Yield();
        await _neverCompletes.Task.ConfigureAwait(false);
        GC.KeepAlive(id);
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Summary");
        DocAssert.HasKeyValue(doc, "Suspended (awaiting)");
        var suspendedStr = DocAssert.GetKeyValue(doc, "Suspended (awaiting)");
        Assert.True(
            suspendedStr is not null && long.TryParse(suspendedStr.Replace(",", ""), out long cnt) && cnt >= 101,
            $"Expected Suspended (awaiting) ≥ 101 (we planted 101). Got: '{suspendedStr}'");
        DocAssert.AlertContains(doc, "suspended");
        DocAssert.TableByHeadersHasMinRows(doc, 1, "state machine table",
            "Method", "State", "Count", "%", "Await Hint");
        DocAssert.AnyTableContainsText(doc, "SuspendedWorker",
            "our 101 SuspendedWorker state machines must be listed");
    }
}
