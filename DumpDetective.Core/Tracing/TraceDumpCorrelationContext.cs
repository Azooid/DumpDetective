using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// Input payload passed to plugin-defined trace-dump correlation rules.
/// Each field corresponds to one built-in analyzer output and may be null
/// when that analyzer produced no data for the current trace.
/// </summary>
public sealed record TraceDumpCorrelationContext(
    DumpSnapshot              Snapshot,
    AllocTraceData?           Alloc,
    GcTraceData?              Gc,
    ContentionTraceData?      Contention,
    ExceptionsTraceData?      Exceptions,
    ThreadPoolStarvationData? Starvation,
    HttpTraceData?            Http,
    AsyncTraceData?           Async,
    SqlTraceData?             Sql,
    CpuTraceData?             Cpu,
    FinalizerTraceData?       Finalizer,
    AllocationBurstData?      AllocBurst,
    LohTraceData?             Loh,
    ConnectionPoolTraceData?  ConnectionPool,
    DeadlockPatternData?      Deadlock,
    RetryStormData?           RetryStorm,
    HandleLeakTraceData?      HandleLeak,
    IReadOnlyDictionary<string, long>? RetainedByType);
