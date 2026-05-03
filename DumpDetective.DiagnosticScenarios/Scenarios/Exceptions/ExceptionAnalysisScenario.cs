using DumpDetective.Core.Models;
using DumpDetective.DiagnosticScenarios;

namespace DumpDetective.DiagnosticScenarios.Scenarios.Exceptions;

// 5 exception types × 30 instances each.

public sealed class ExceptionAnalysisScenario : IScenario
{
    private static readonly List<Exception> _exceptions = [];
    public string CommandName => "exception-analysis";
    public string Description => "5 exception types × 30 instances each (150 total).";

    public void Setup()
    {
        const int n = 30;
        for (int i = 0; i < n; i++) _exceptions.Add(new InvalidOperationException($"State invalid at step {i}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new TimeoutException($"Timed out waiting for resource {i}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new IOException($"Disk error on shard {i % 8}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new ArgumentNullException($"param{i}", $"param{i} was null."));
        for (int i = 0; i < n; i++)
        {
            try { throw new KeyNotFoundException($"Key 'order-{i}' not found."); }
            catch (Exception ex) { _exceptions.Add(ex); }
        }
    }

    public void Validate(ReportDoc doc)
    {
        DocAssert.HasSection(doc, "Exceptions on Heap");
        DocAssert.TableByHeadersHasMinRows(doc, 5, "exception type table",
            "Exception Type", "Count", "Active", "HResult");
        DocAssert.AnyTableContainsText(doc, "InvalidOperationException");
        DocAssert.AnyTableContainsText(doc, "TimeoutException");
        DocAssert.AnyTableContainsText(doc, "IOException");
        DocAssert.AnyTableContainsText(doc, "ArgumentNullException");
        DocAssert.AnyTableContainsText(doc, "KeyNotFoundException");
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length > 1 && long.TryParse(r[1].Replace(",", ""), out long v) && v >= 30),
            "at least one exception type with Count ≥ 30 (we planted 30 per type)");
    }
}
