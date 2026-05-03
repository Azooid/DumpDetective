using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Async;

public sealed class AsyncStacksScenario : IScenario
{
    private static readonly TaskCompletionSource _neverCompletes = new();
    private static readonly List<Task> _suspended = [];

    public string CommandName => "async-stacks";
    public string Description => "101 async state machines suspended on a TaskCompletionSource.";

    public void Setup()
    {
        for (int i = 0; i < 101; i++)
            _suspended.Add(SuspendedWorker(i));
    }

    private static async Task SuspendedWorker(int id)
    {
        await Task.Yield();
        await _neverCompletes.Task.ConfigureAwait(false);
        GC.KeepAlive(id);
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always rendered
        DocAssert.HasSection(doc, "Summary");

        // KV summary: "Suspended (awaiting)" must show the count of suspended state machines
        DocAssert.HasKeyValue(doc, "Suspended (awaiting)");
        var suspendedStr = DocAssert.GetKeyValue(doc, "Suspended (awaiting)");
        Assert.True(
            suspendedStr is not null && long.TryParse(suspendedStr.Replace(",", ""), out long cnt) && cnt >= 101,
            $"Expected Suspended (awaiting) ≥ 101 (we planted 101). Got: '{suspendedStr}'");

        // Alert must fire because 101 > 100 threshold in AsyncStacksReport
        DocAssert.AlertContains(doc, "suspended");

        // State machine table: [Method, State, Count, %, Await Hint]
        // We planted 101 suspended state machines, so ≥ 1 row grouping them
        DocAssert.TableByHeadersHasMinRows(doc, 1, "state machine table",
            "Method", "State", "Count", "%", "Await Hint");

        // Our SuspendedWorker method must appear in the state machine table
        DocAssert.AnyTableContainsText(doc, "SuspendedWorker",
            "our 101 SuspendedWorker state machines must be listed");
    }
}
