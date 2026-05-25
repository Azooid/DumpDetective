using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies DNS name resolution events from System.Net.NameResolution:
///   DnsNameResolution/Start|Stop, Resolution/Fail.
/// </summary>
internal sealed class DnsClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["System.Net.NameResolution"];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "DnsNameResolution") || Contains(name, "NameResolution") ||
            Contains(name, "DnsResolut")         || Contains(name, "Resolution/"))
        {
            if (Contains(name, "Start")) return DnsResolutionStart;
            if (Contains(name, "Stop"))  return DnsResolutionStop;
            if (Contains(name, "Fail"))  return DnsResolutionFailed;
        }
        return Unknown;
    }
}
