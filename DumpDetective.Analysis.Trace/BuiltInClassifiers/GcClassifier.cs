using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies all GC events from Microsoft-Windows-DotNETRuntime:
/// GC/Start|Stop, SuspendEE, RestartEE, HeapStats, AllocationTick,
/// FinalizeObject, GCHandle/Created|Destroyed, extended bulk/heap events.
/// </summary>
internal sealed class GcClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } = ["Microsoft-Windows-DotNETRuntime"];

    public TraceEventKind Classify(string provider, string name)
    {
        // GCHandle events — must check before generic GC* matching
        if (Contains(name, "GCHandle") || Contains(name, "GCCreateConcurrent"))
        {
            if (Contains(name, "Created") || Contains(name, "Create"))    return GCHandleCreated;
            if (Contains(name, "Destroyed") || Contains(name, "Destroy")) return GCHandleDestroyed;
        }

        // Order matters: more-specific checks before generic GCStart/Stop
        if (Contains(name, "SuspendEEStop")   || Contains(name, "GC/SuspendEEStop"))  return GCSuspendEEStop;
        if (Contains(name, "SuspendEEStart")  || Contains(name, "GC/SuspendEEStart")) return GCSuspendEEStart;
        if (Contains(name, "RestartEEStop")   || Contains(name, "GC/RestartEEStop"))  return GCRestartEEStop;
        if (Contains(name, "RestartEEStart")  || Contains(name, "GC/RestartEEStart")) return GCRestartEEStart;
        if (Contains(name, "FinalizeObject"))                                           return GCFinalizeObject;
        if (Contains(name, "GCHeapStats")     || Contains(name, "GC/HeapStats"))       return GCHeapStats;
        if (Contains(name, "AllocationTick")  || Contains(name, "GC/AllocationTick"))  return GCAllocationTick;
        if (Contains(name, "GC/Start")        || EndsWith(name, "GCStart"))            return GCStart;
        if (Contains(name, "GC/Stop")         || EndsWith(name, "GCStop"))             return GCStop;

        // Extended GC events
        if (Contains(name, "FinalizersStart") || Contains(name, "GC/FinalizersStart")) return GCFinalizersStart;
        if (Contains(name, "FinalizersStop")  || Contains(name, "GC/FinalizersStop"))  return GCFinalizersStop;
        if (Contains(name, "GC/Triggered")    || EndsWith(name, "GCTriggered"))        return GCTriggered;
        if (Contains(name, "CreateSegment"))    return GCCreateSegment;
        if (Contains(name, "CommittedUsage"))   return GCCommittedUsage;
        if (Contains(name, "GlobalHeapHistory")) return GCGlobalHeapHistory;
        if (Contains(name, "PerHeapHistory"))   return GCPerHeapHistory;
        if (Contains(name, "GenerationRange"))  return GCGenerationRange;
        if (Contains(name, "PinObjectAtGCTime")) return GCPinObjectAtGCTime;
        if (Contains(name, "SetGCHandle"))      return GCSetGCHandle;
        if (Contains(name, "MarkWithType"))     return GCMarkWithType;
        if (Contains(name, "GC/Join")           || EndsWith(name, "GCJoin")) return GCJoin;
        // Bulk GC data events (BulkNode|BulkEdge|BulkRoot*|BulkSurviving*|BulkMoved*)
        if (Contains(name, "GCBulk") || Contains(name, "BulkNode") || Contains(name, "BulkEdge") ||
            Contains(name, "BulkRoot") || Contains(name, "BulkSurviving") || Contains(name, "BulkMoved"))
            return GCBulkData;

        return Unknown;
    }
}
