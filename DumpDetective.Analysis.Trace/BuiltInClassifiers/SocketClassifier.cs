using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies socket events from System.Net.Sockets:
///   Connect/Start|Stop|Fail, Send/Start|Stop, Receive/Start|Stop, Accept/Start|Stop.
/// </summary>
internal sealed class SocketClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["System.Net.Sockets"];

    public TraceEventKind Classify(string provider, string name)
    {
        // Accept (classified first to avoid falling into generic Socket connect/send/receive)
        if (Contains(name, "Accept/") || Contains(name, "SocketAccept"))
        {
            if (EndsWith(name, "Start")) return SocketAcceptStart;
            if (EndsWith(name, "Stop"))  return SocketAcceptStop;
        }

        if (Contains(name, "Socket") || Contains(provider, "Sockets"))
        {
            if (Contains(name, "Connect"))
            {
                if (EndsWith(name, "Start"))      return SocketConnectStart;
                if (EndsWith(name, "Stop"))       return SocketConnectStop;
                if (Contains(name, "Fail"))       return SocketConnectFailed;
            }
            if (Contains(name, "Send"))
            {
                if (EndsWith(name, "Start")) return SocketSendStart;
                if (EndsWith(name, "Stop"))  return SocketSendStop;
            }
            if (Contains(name, "Receive"))
            {
                if (EndsWith(name, "Start")) return SocketReceiveStart;
                if (EndsWith(name, "Stop"))  return SocketReceiveStop;
            }
        }

        return Unknown;
    }
}
