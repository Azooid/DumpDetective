using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Normalized metadata for a single event type, computed ONCE per unique
/// <c>(EventName, ProviderName)</c> pair by <see cref="TraceEventDispatcher"/>
/// and passed to every <see cref="ITraceEventConsumer"/>.
///
/// Consumers can switch on <see cref="Kind"/> for fast, allocation-free routing
/// and fall back to raw string checks on <see cref="EventName"/> /
/// <see cref="ProviderName"/> for finer-grained discrimination or for events
/// that are not (yet) classified by <see cref="EventNormalizer"/>.
///
/// External plugins implement <see cref="ITraceEventConsumer"/> and receive
/// the same <see cref="TraceEventMeta"/> as built-in analyzers.  If the plugin
/// emits events that <see cref="EventNormalizer"/> does not recognise,
/// <see cref="Kind"/> will be <see cref="TraceEventKind.Unknown"/> and the
/// plugin simply matches on <see cref="ProviderName"/> or <see cref="EventName"/>.
/// </summary>
public readonly struct TraceEventMeta
{
    /// <summary>Raw <c>TraceEvent.EventName</c> (never null; empty string if unset).</summary>
    public string EventName    { get; }

    /// <summary>Raw <c>TraceEvent.ProviderName</c> (never null; empty string if unset).</summary>
    public string ProviderName { get; }

    /// <summary>
    /// Normalised classification produced by <see cref="EventNormalizer.Classify"/>.
    /// <see cref="TraceEventKind.Unknown"/> means the event was not recognised —
    /// use <see cref="EventName"/> / <see cref="ProviderName"/> for matching instead.
    /// </summary>
    public TraceEventKind Kind { get; }

    /// <summary><c>true</c> when <see cref="Kind"/> is not <see cref="TraceEventKind.Unknown"/>.</summary>
    public bool IsKnown => Kind != TraceEventKind.Unknown;

    public TraceEventMeta(string eventName, string providerName, TraceEventKind kind)
    {
        EventName    = eventName;
        ProviderName = providerName;
        Kind         = kind;
    }
}
