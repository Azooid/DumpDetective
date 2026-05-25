namespace DumpDetective.Core.Tracing;

/// <summary>
/// Classifies a raw trace event by <c>(providerName, eventName)</c> into a
/// <see cref="TraceEventKind"/>.
///
/// <para>Built-in classification is handled by <c>EventNormalizer</c>.
/// Register additional classifiers via <c>EventNormalizer.Register</c>
/// to support custom <c>EventSource</c> providers or plugin-defined event kinds.</para>
///
/// <para>Classification is called <b>once per unique (eventName, providerName) pair</b>
/// by <c>TraceEventDispatcher</c> and the result is cached.  Implementations must be
/// thread-safe for concurrent reads but do not need to handle concurrent writes
/// (all registration must complete before the first trace pass).</para>
/// </summary>
public interface ITraceEventClassifier
{
    /// <summary>
    /// Returns the <see cref="TraceEventKind"/> for the given event identity,
    /// or <see cref="TraceEventKind.Unknown"/> if this classifier does not recognise it.
    /// Must never return <c>null</c>.
    /// </summary>
    TraceEventKind Classify(string providerName, string eventName);
}
