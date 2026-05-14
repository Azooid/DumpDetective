using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Processes a single ETW / EventPipe event during a shared trace-file pass.
///
/// Lifecycle:
///   1. <see cref="Consume"/> is called once per event in chronological order.
///   2. <see cref="OnComplete"/> is called exactly once after the last event,
///      even if the pass threw (the dispatcher calls it from a finally block).
///
/// Implementations must be single-threaded (the dispatcher is sequential).
/// </summary>
public interface ITraceEventConsumer
{
    /// <param name="eventName">Pre-extracted <c>ev.EventName ?? ""</c> — computed once by the
    /// dispatcher so consumers do not each re-read the property.</param>
    /// <param name="processName">Pre-extracted <c>ev.ProcessName ?? ""</c> — same reason.</param>
    /// <param name="timestampMs">Pre-extracted <c>ev.TimeStampRelativeMSec</c> (used in 24/29 consumers).</param>
    /// <param name="threadId">Pre-extracted <c>ev.ThreadID</c> (used in 16/29 consumers).</param>
    void Consume(TraceEvent ev, string eventName, string processName, double timestampMs, int threadId);

    /// <summary>
    /// Called once per unique event name to build the dispatcher's routing table.
    /// Return <c>false</c> to permanently skip all events with this name for this consumer.
    /// The dispatcher caches the result — this is invoked at most once per unique event name.
    /// Default: <c>true</c> (receive all events).
    /// </summary>
    bool WantsEvent(string eventName) => true;

    void OnComplete();
}
