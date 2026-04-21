namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenario for: exception-analysis
// Populates the heap with 200 Exception instances across 5 types.
// DumpDetective exception-analysis enumerates heap objects whose type derives
// from System.Exception and groups them by type, HResult, and message.
internal static class ExceptionScenarios
{
    private static readonly List<Exception> _exceptions = [];

    public static IResult TriggerExceptionAnalysis()
    {
        const int perType = 40; // 5 types × 40 = 200 total

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new InvalidOperationException($"State machine in invalid state {i}: expected Idle, was Running."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new TimeoutException($"Operation timed out after 30 000 ms waiting for resource pool (request #{i})."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new OutOfMemoryException($"Allocation of {i * 1024} bytes failed — heap exhausted."));

        for (int i = 0; i < perType; i++)
            _exceptions.Add(new IOException($"Disk I/O error on path C:\\data\\shard{i % 8}.bin at offset {i * 4096}."));

        for (int i = 0; i < perType; i++)
        {
            try { throw new ArgumentNullException($"param{i}", $"Required parameter 'param{i}' was null."); }
            catch (Exception ex) { _exceptions.Add(ex); } // captured with stack trace
        }

        return Results.Ok(new
        {
            message = $"{_exceptions.Count} exceptions on heap across 5 types.",
            types = new[] { "InvalidOperationException", "TimeoutException", "OutOfMemoryException", "IOException", "ArgumentNullException" },
            command = "DumpDetective exception-analysis <dump.dmp>",
        });
    }

    public static string Status => $"exception-analysis: {_exceptions.Count} Exception objects";

    public static void Reset() => _exceptions.Clear();
}
