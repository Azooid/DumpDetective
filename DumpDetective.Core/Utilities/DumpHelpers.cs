using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Utilities;

public static class DumpHelpers
{
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F2} MB",
        >= 1_024         => $"{bytes / 1_024.0:F2} KB",
        _                => $"{bytes} B"
    };

    public static bool IsSystemType(string name) =>
        name.StartsWith("System.",                StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.",             StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MS.",                    StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Internal.",              StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Windows.",               StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Interop.",               StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FxResources.",           StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("System_Private_CoreLib", StringComparison.OrdinalIgnoreCase);

    public static bool IsExceptionType(ClrType type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.Name == "System.Exception")
                return true;
        return false;
    }

    public static (ClrRuntime? Runtime, DataTarget DataTarget) OpenDump(string dumpPath)
    {
        var dataTarget = DataTarget.LoadDump(dumpPath);
        var runtime    = dataTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();
        return (runtime, dataTarget);
    }

    public static string SegmentKindLabel(ClrHeap heap, ulong address)
    {
        var seg = heap.GetSegmentByAddress(address);
        return seg?.Kind switch
        {
            GCSegmentKind.Large  => "LOH",
            GCSegmentKind.Pinned => "POH",
            GCSegmentKind.Frozen => "Frozen",
            _                    => "Gen"
        };
    }
}
