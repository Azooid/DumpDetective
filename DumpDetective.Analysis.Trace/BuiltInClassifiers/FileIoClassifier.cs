using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies kernel file I/O events from Microsoft-Windows-Kernel-File:
///   FileIO/Read|Write|Create|Close|Flush.
/// </summary>
internal sealed class FileIoClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-Kernel-File"];

    public TraceEventKind Classify(string provider, string name)
    {
        if (Contains(name, "FileIO") || Contains(name, "File/") || Contains(name, "KernelFile"))
        {
            if (Contains(name, "Read"))   return FileRead;
            if (Contains(name, "Write"))  return FileWrite;
            if (Contains(name, "Create")) return FileCreate;
            if (Contains(name, "Close"))  return FileClose;
            if (Contains(name, "Flush"))  return FileFlush;
        }
        return Unknown;
    }
}
