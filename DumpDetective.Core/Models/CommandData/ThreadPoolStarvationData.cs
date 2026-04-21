namespace DumpDetective.Core.Models.CommandData;

public sealed record ThreadPoolStarvationData(
    string                              TraceInfo,
    int                                 TotalEvents,
    IReadOnlyList<WaitEventSummary>     WaitEvents,
    IReadOnlyList<TpAdjustmentRecord>   Adjustments,
    int                                 StarvationAdjustmentCount,
    uint                                TpMaxActive,
    uint                                TpFinalActive,
    IReadOnlyDictionary<string, int>    EventCounts);

public sealed record WaitEventSummary(
    int                   ThreadId,
    string                WaitSourceName,
    IReadOnlyList<string> TopFrames);

public sealed record TpAdjustmentRecord(
    string  Timestamp,
    uint    NewCount,
    string  ReasonName,
    double  AverageThroughput);
