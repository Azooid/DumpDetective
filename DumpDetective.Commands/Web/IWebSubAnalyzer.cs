namespace DumpDetective.Commands.Web;

/// <summary>
/// Encapsulates a single web-trace sub-analyzer + its report renderer so that
/// <see cref="WebAnalyzeCommand"/> can drive every one of them via a single loop
/// instead of one copy-pasted block per command — mirrors
/// <c>DumpDetective.Commands.Trace.ITraceSubAnalyzer</c>.
///
/// Unlike the trace-side interface, this one needs no "consumer" single-pass-dispatch
/// half: <see cref="WebTraceContext.Open"/> already reduces the whole file into one
/// <c>WebTraceData</c> (parsed once, cached), so every sub-analyzer just reads from that
/// already-computed data — there's no raw per-event stream left to fan out over.
/// </summary>
public interface IWebSubAnalyzer
{
    string Key { get; }
    string SectionTitle { get; }

    /// <summary>
    /// Runs analysis against already-parsed trace data, writes the captured report into
    /// <paramref name="captured"/> and the typed result into <paramref name="results"/>
    /// under <see cref="Key"/>, and returns the findings for the caller's Action Queue.
    /// </summary>
    IReadOnlyList<Finding> Run(
        WebTraceData trace, string traceFileName,
        Dictionary<string, ReportDoc> captured,
        Dictionary<string, object?> results);
}
