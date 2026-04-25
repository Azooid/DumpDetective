using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Consumers;

/// <summary>
/// Typed wrapper carrying the pre-built task/work-item count maps from
/// <see cref="ThreadPoolConsumer"/>, used as the cache key in
/// <c>DumpContext.SetAnalysis&lt;ThreadPoolConsumerCache&gt;</c>.
/// </summary>
internal sealed class ThreadPoolConsumerCache(
    Dictionary<string, int> taskCounts,
    Dictionary<string, int> workItems)
{
    public Dictionary<string, int> TaskCounts { get; } = taskCounts;
    public Dictionary<string, int> WorkItems  { get; } = workItems;
}

/// <summary>
/// Accumulates Task state counts and work-item type counts for <c>ThreadPoolAnalyzer</c>.
/// Pre-populated during <c>CollectHeapObjectsCombined</c> and cached via
/// <c>DumpContext.SetAnalysis&lt;ThreadPoolConsumerCache&gt;</c>.
/// Task state is decoded from the <c>m_stateFlags</c> int field using the same
/// internal flag constants as the .NET runtime (checked in priority order:
/// Faulted → Canceled → RanToCompletion → Running → WaitingToRun → WaitingForActivation).
/// Work items are counted by type name — any type matching <see cref="HeapTypeMeta.IsWorkItem"/>
/// (e.g. <c>QueueUserWorkItemCallback</c>, <c>ThreadPoolWorkItem</c>) is tallied.
/// </summary>
internal sealed class ThreadPoolConsumer : IHeapObjectConsumer
{
    private const int TASK_STATE_RAN_TO_COMPLETION      = 0x1000000;
    private const int TASK_STATE_CANCELED               = 0x0400000;
    private const int TASK_STATE_FAULTED                = 0x0200000;
    private const int TASK_STATE_DELEGATE_INVOKED       = 0x0080000;
    private const int TASK_STATE_STARTED                = 0x0010000;
    private const int TASK_STATE_WAITING_FOR_ACTIVATION = 0x0001000;

    public readonly Dictionary<string, int> TaskCounts = new(StringComparer.Ordinal)
    {
        ["WaitingToRun"]         = 0,
        ["Running"]              = 0,
        ["WaitingForActivation"] = 0,
        ["RanToCompletion"]      = 0,
        ["Faulted"]              = 0,
        ["Canceled"]             = 0,
        ["Other"]                = 0,
    };

    public readonly Dictionary<string, int> WorkItems = new(StringComparer.Ordinal);

    public void Consume(in ClrObject obj, HeapTypeMeta meta, ClrHeap heap)
    {
        if (meta.IsTask)
        {
            string label = GetTaskStateLabel(obj);
            ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(TaskCounts, label, out _);
            c++;
        }
        else if (meta.IsWorkItem)
        {
            ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(WorkItems, meta.Name, out _);
            c++;
        }
    }

    public void OnWalkComplete() { }

    public IHeapObjectConsumer CreateClone() => new ThreadPoolConsumer();

    public void MergeFrom(IHeapObjectConsumer other)
    {
        var src = (ThreadPoolConsumer)other;
        foreach (var (label, count) in src.TaskCounts)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(TaskCounts, label, out _);
            dst += count;
        }
        foreach (var (name, count) in src.WorkItems)
        {
            ref int dst = ref CollectionsMarshal.GetValueRefOrAddDefault(WorkItems, name, out _);
            dst += count;
        }
    }

    private static string GetTaskStateLabel(in ClrObject obj)
    {
        try
        {
            int flags = obj.ReadField<int>("m_stateFlags");
            if ((flags & TASK_STATE_FAULTED) != 0)                return "Faulted";
            if ((flags & TASK_STATE_CANCELED) != 0)               return "Canceled";
            if ((flags & TASK_STATE_RAN_TO_COMPLETION) != 0)      return "RanToCompletion";
            if ((flags & TASK_STATE_DELEGATE_INVOKED) != 0)       return "Running";
            if ((flags & TASK_STATE_STARTED) != 0)                return "WaitingToRun";
            if ((flags & TASK_STATE_WAITING_FOR_ACTIVATION) != 0) return "WaitingForActivation";
        }
        catch { }
        return "Other";
    }
}
