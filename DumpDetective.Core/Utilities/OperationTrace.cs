namespace DumpDetective.Core.Utilities;

/// <summary>
/// Per-thread operation trace: records label + elapsed-ms pairs during a
/// <c>BuildReport</c> run, then formats them into compact two-per-line summary
/// strings shown in the parallel full-analyze progress display.
/// <c>[ThreadStatic]</c> so parallel workers each have their own trace.
/// </summary>
public static class OperationTrace
{
    [ThreadStatic] private static List<(string Label, long Ms)>? _trace;

    /// <summary>
    /// Activates tracing on this thread. Call immediately before
    /// <c>BuildReport</c>. Every subsequent <see cref="Add"/> call will record
    /// its label and elapsed milliseconds into the trace.
    /// </summary>
    public static void BeginTrace() => _trace = new List<(string, long)>(8);

    /// <summary>
    /// Records a label + elapsed-ms pair into the active trace (no-op if no
    /// trace is active). Called by <see cref="CommandBase.RunStatus"/> after
    /// each operation completes.
    /// </summary>
    internal static void Add(string label, long ms) => _trace?.Add((label, ms));

    /// <summary>
    /// Ends tracing, formats the recorded entries into sub-lines (2 per line),
    /// and clears the trace list. Returns <see langword="null"/> when nothing
    /// was traced (command used only cached snapshot data).
    /// </summary>
    public static string[]? EndTrace()
    {
        var t = _trace;
        _trace = null;
        if (t is null || t.Count == 0) return null;

        var formatted = new List<string>(t.Count);
        foreach (var (label, ms) in t)
        {
            string elapsed = ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";
            // Strip trailing "..." from messages like "Scanning GC handles..."
            string clean = label.EndsWith("...") ? label[..^3].TrimEnd() : label;
            formatted.Add($"{clean} • {elapsed}");
        }

        // Pack 2 entries per sub-line for compact display
        var lines = new List<string>();
        for (int i = 0; i < formatted.Count; i += 2)
        {
            lines.Add(i + 1 < formatted.Count
                ? $"{formatted[i]}  |  {formatted[i + 1]}"
                : formatted[i]);
        }
        return lines.ToArray();
    }
}
