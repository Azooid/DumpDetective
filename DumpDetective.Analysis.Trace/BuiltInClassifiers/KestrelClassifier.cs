using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies Microsoft-AspNetCore-Server-Kestrel events.
/// Handles both fully-qualified names (e.g. "KestrelConnectionStart") that may appear
/// from any provider, and bare names (e.g. "ConnectionStart") emitted on the Kestrel
/// provider in EventPipe traces.
/// </summary>
internal sealed class KestrelClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-AspNetCore-Server-Kestrel"];

    public TraceEventKind Classify(string provider, string name)
    {
        bool isKestrel = Contains(name, "Kestrel") || Contains(provider, "Kestrel");
        if (!isKestrel) return Unknown;

        if (Contains(name, "ConnectionStart") || (Contains(name, "Connection") && EndsWith(name, "Start")))
            return KestrelConnectionStart;
        if (Contains(name, "ConnectionStop") || (Contains(name, "Connection") && EndsWith(name, "Stop")))
            return KestrelConnectionStop;
        if (Contains(name, "ConnectionRejected") || Contains(name, "Reject"))
            return KestrelConnectionRejected;
        if (Contains(name, "RequestError") || (Contains(name, "Request") && Contains(name, "Error")))
            return KestrelRequestError;
        if (Contains(name, "QueueStart") || (Contains(name, "Queue") && EndsWith(name, "Start")))
            return KestrelConnectionQueueStart;
        if (Contains(name, "QueueStop") || (Contains(name, "Queue") && EndsWith(name, "Stop")))
            return KestrelConnectionQueueStop;
        if (Contains(name, "RequestStart") || Contains(name, "Request/Start")) return KestrelRequestStart;
        if (Contains(name, "RequestStop")  || Contains(name, "Request/Stop"))  return KestrelRequestStop;
        if (Contains(name, "TlsHandshake") && EndsWith(name, "Start")) return KestrelTlsHandshakeStart;
        if (Contains(name, "TlsHandshake") && EndsWith(name, "Stop"))  return KestrelTlsHandshakeStop;

        return Unknown;
    }
}
