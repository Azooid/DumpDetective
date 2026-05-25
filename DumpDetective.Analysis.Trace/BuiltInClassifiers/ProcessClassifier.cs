using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies process lifecycle and CPU-sample events from:
///   Microsoft-DotNETCore-SampleProfiler (cpu-sampling, SampledProfile)
///   Microsoft-Windows-Kernel-Process    (Process/Start|Stop)
///   Windows Kernel                      (PerfInfo/SampledProfile)
/// </summary>
internal sealed class ProcessClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } =
    [
        "Microsoft-DotNETCore-SampleProfiler",
        "Microsoft-Windows-Kernel-Process",
        "Windows Kernel",
    ];

    public TraceEventKind Classify(string provider, string name)
    {
        // CPU sampling — high-frequency; check before generic Process* patterns
        if (Contains(name, "PerfInfo")       || Contains(name, "SampledProfile") ||
            Contains(name, "cpu-sampling")   || Contains(provider, "SampleProfiler"))
            return CpuSample;

        // Process lifecycle
        if (Contains(name, "ProcessStart") ||
            (Contains(name, "Process/Start") && !Contains(name, "GetRequest")))
            return ProcessStart;
        if (Contains(name, "ProcessStop") || Contains(name, "Process/Stop"))
            return ProcessStop;

        return Unknown;
    }
}
