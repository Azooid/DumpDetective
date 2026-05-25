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
    /// <param name="meta">Normalised metadata computed once per unique (EventName, ProviderName) pair
    /// by the dispatcher.  Use <c>meta.Kind</c> for fast kind-based routing and fall back to
    /// <c>meta.EventName</c> / <c>meta.ProviderName</c> for fine-grained checks or for events
    /// classified as <see cref="DumpDetective.Core.Tracing.TraceEventKind.Unknown"/>.</param>
    /// <param name="processName">Pre-extracted <c>ev.ProcessName ?? ""</c>.</param>
    /// <param name="timestampMs">Pre-extracted <c>ev.TimeStampRelativeMSec</c>.</param>
    /// <param name="threadId">Pre-extracted <c>ev.ThreadID</c>.</param>
    void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId);

    /// <summary>
    /// Called ONCE per unique (EventName, ProviderName) pair to build the dispatcher's routing
    /// table.  Return <c>false</c> to permanently skip all events with this meta for this consumer.
    /// The dispatcher caches the result — this is invoked at most once per unique event type.
    /// Default: <c>true</c> (receive all events).
    /// </summary>
    bool WantsEvent(in TraceEventMeta meta) => true;

    void OnComplete();
}
