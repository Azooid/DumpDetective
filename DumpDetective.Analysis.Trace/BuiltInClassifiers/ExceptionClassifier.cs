using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies exception events from Microsoft-Windows-DotNETRuntime:
///   Exception/Start (first-chance throw), ExceptionCatch/Start|Stop.
/// </summary>
internal sealed class ExceptionClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-DotNETRuntime"];

    public TraceEventKind Classify(string provider, string name)
    {
        // More-specific catch handlers before the generic throw
        if (Contains(name, "ExceptionFinally/Stop")  || EndsWith(name, "ExceptionFinallyStop"))  return ExceptionFinallyStop;
        if (Contains(name, "ExceptionFinally/Start") || EndsWith(name, "ExceptionFinallyStart")) return ExceptionFinallyStart;
        if (Contains(name, "ExceptionCatch/Stop")  || EndsWith(name, "ExceptionCatchStop"))  return ExceptionCatchStop;
        if (Contains(name, "ExceptionCatch/Start") || EndsWith(name, "ExceptionCatchStart")) return ExceptionCatchStart;
        // Exception/Stop = .NET Framework exception-handling-complete event (after catch runs)
        if (Contains(name, "Exception/Stop")       || EndsWith(name, "ExceptionHandled"))    return ExceptionHandled;
        if (Contains(name, "Exception/Start")      || EndsWith(name, "ExceptionThrown")
                                                   || EndsWith(name, "Exception"))            return ExceptionThrown;
        return Unknown;
    }
}
