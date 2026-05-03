using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Exceptions;

public sealed class ExceptionAnalysisScenario : IScenario
{
    // Static — keeps exception objects alive in the dump
    private static readonly List<Exception> _exceptions = [];

    public string CommandName => "exception-analysis";
    public string Description => "5 exception types × 30 instances each (150 total).";

    public void Setup()
    {
        const int perType = 30;

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new InvalidOperationException($"State invalid at step {i}."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new TimeoutException($"Timed out waiting for resource {i}."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new IOException($"Disk error on shard {i % 8}."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new ArgumentNullException($"param{i}", $"param{i} was null."));

        for (int i = 0; i < perType; i++)
        {
            // Captured with a real stack trace
            try { throw new KeyNotFoundException($"Key 'order-{i}' was not found."); }
            catch (Exception ex) { _exceptions.Add(ex); }
        }
    }

    public void Validate(ReportDoc doc)
    {
        // Section: "1. Exceptions on Heap" is always emitted when exceptions are found
        DocAssert.HasSection(doc, "Exceptions on Heap");

        // Exception type table: [Exception Type, Count, Active, HResult, Inner Exception, Sample Message]
        // We planted 5 distinct types — all must appear (top=20 default)
        DocAssert.TableByHeadersHasMinRows(doc, 5, "exception type table",
            "Exception Type", "Count", "Active", "HResult");

        // Verify each of the 5 planted exception types is detected
        DocAssert.AnyTableContainsText(doc, "InvalidOperationException",
            "30 InvalidOperationException instances we planted");
        DocAssert.AnyTableContainsText(doc, "TimeoutException",
            "30 TimeoutException instances we planted");
        DocAssert.AnyTableContainsText(doc, "IOException",
            "30 IOException instances we planted");
        DocAssert.AnyTableContainsText(doc, "ArgumentNullException",
            "30 ArgumentNullException instances we planted");
        DocAssert.AnyTableContainsText(doc, "KeyNotFoundException",
            "30 KeyNotFoundException instances with real stack traces we planted");

        // Each type has 30 instances — at least one Count cell must be ≥ 30
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length > 1 && long.TryParse(r[1].Replace(",", ""), out long v) && v >= 30),
            "at least one exception type with Count ≥ 30 (we planted 30 per type)");
    }
}
