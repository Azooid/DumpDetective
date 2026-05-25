using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies assembly and module loading events from Microsoft-Windows-DotNETRuntime:
///   Loader/AssemblyLoad|ModuleLoad, AssemblyLoader/KnownPathProbed|ResolutionAttempted|Start|Stop.
/// </summary>
internal sealed class AssemblyClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-DotNETRuntime"];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "AssemblyLoader"))
        {
            if (Contains(name, "KnownPathProbed"))     return AssemblyLoaderKnownPathProbed;
            if (Contains(name, "ResolutionAttempted")) return AssemblyLoaderResolutionAttempted;
            if (EndsWith(name, "Start"))               return AssemblyLoaderStart;
            if (EndsWith(name, "Stop"))                return AssemblyLoaderStop;
        }

        if (Contains(name, "Loader/AssemblyLoad") || EndsWith(name, "AssemblyLoad"))
            return AssemblyLoaded;

        // Guard against DomainModuleLoad being misclassified as ModuleLoad
        if ((Contains(name, "Loader/ModuleLoad") || EndsWith(name, "ModuleLoad"))
             && !Contains(name, "DomainModuleLoad"))
            return ModuleLoaded;

        return Unknown;
    }
}
