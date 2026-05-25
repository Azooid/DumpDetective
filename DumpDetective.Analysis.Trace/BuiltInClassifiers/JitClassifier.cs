using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies JIT, TieredCompilation and TypeLoad events from
/// Microsoft-Windows-DotNETRuntime:
/// Method/JittingStarted|LoadVerbose|InliningSucceeded|InliningFailedAnsi|
/// R2RGetEntryPoint|UnloadVerbose|MemoryAllocatedForJitCode|TailCallSucceeded,
/// TieredCompilation/BackgroundJitStart|Stop|Pause|Resume,
/// TypeLoad/Start|Stop.
/// </summary>
internal sealed class JitClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-DotNETRuntime"];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "JittingStarted")    || Contains(name, "Method/JittingStarted"))  return JitMethodStart;
        if (Contains(name, "LoadVerbose")       || Contains(name, "Method/LoadVerbose"))      return JitMethodLoad;
        if (Contains(name, "InliningSucceeded"))                                               return JitInliningSucceeded;
        if (Contains(name, "InliningFailed"))                                                  return JitInliningFailed;
        if (Contains(name, "R2RGetEntryPoint"))                                                return JitR2RGetEntryPoint;
        if (Contains(name, "UnloadVerbose"))                                                   return JitMethodUnload;
        if (Contains(name, "MemoryAllocatedForJitCode"))                                       return JitMemoryAllocated;
        if (Contains(name, "MethodDetails"))                                                   return JitMethodDetails;
        if (Contains(name, "TailCallSucceeded"))                                               return JitTailCallSucceeded;

        // Tiered compilation
        if (Contains(name, "TieredCompilation"))
        {
            if (Contains(name, "BackgroundJitStart")) return TieredCompilationBackgroundStart;
            if (Contains(name, "BackgroundJitStop"))  return TieredCompilationBackgroundStop;
            if (Contains(name, "Pause"))              return TieredCompilationPause;
            if (Contains(name, "Resume"))             return TieredCompilationResume;
        }

        // Type loading
        if (Contains(name, "TypeLoad/Start") || Contains(name, "TypeLoadStart")) return TypeLoadStart;
        if (Contains(name, "TypeLoad/Stop")  || Contains(name, "TypeLoadStop"))  return TypeLoadStop;

        return Unknown;
    }
}
