using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies contention and WaitHandle events from Microsoft-Windows-DotNETRuntime:
///   Contention/Start|Stop, WaitHandle/Wait/Start|Stop.
/// </summary>
internal sealed class ContentionClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-DotNETRuntime"];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "Contention/Start")   || EndsWith(name, "ContentionStart")) return ContentionStart;
        if (Contains(name, "Contention/Stop")    || EndsWith(name, "ContentionStop"))  return ContentionStop;
        if (Contains(name, "WaitHandleWaitStart") ||
            (Contains(name, "WaitHandleWait") && Contains(name, "Start")))             return WaitHandleWaitStart;
        if (Contains(name, "WaitHandleWaitStop")  ||
            (Contains(name, "WaitHandleWait") && Contains(name, "Stop")))              return WaitHandleWaitStop;
        return Unknown;
    }
}
