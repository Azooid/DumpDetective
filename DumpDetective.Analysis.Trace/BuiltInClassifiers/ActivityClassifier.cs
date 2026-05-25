using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies activity / distributed tracing events from:
///   System.Diagnostics.DiagnosticSource (Activity1/Start|Stop)
///   OpenTelemetry
/// Also handles the provider-level DiagnosticSource activity events
/// (provider itself contains "DiagnosticSource").
/// </summary>
internal sealed class ActivityClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } =
    [
        "System.Diagnostics.DiagnosticSource",
        "OpenTelemetry",
    ];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "Activity1/Start")  || Contains(name, "ActivityStart"))  return ActivityStart;
        if (Contains(name, "Activity1/Stop")   || Contains(name, "ActivityStop"))   return ActivityStop;

        // DiagnosticSource provider: any Start/Stop event is an activity boundary
        if (Contains(provider, "DiagnosticSource"))
        {
            if (Contains(name, "Start")) return ActivityStart;
            if (Contains(name, "Stop"))  return ActivityStop;
        }

        return Unknown;
    }
}
