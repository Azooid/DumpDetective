namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>EventAnalysisAnalyzer</c>.</summary>
public sealed record EventAnalysisData(
    IReadOnlyList<EventLeakGroup> Groups,
    int PublisherInstanceCount = 0);

/// <summary>Aggregated subscriber count for one (publisher type, delegate field) pair.</summary>
public sealed record EventLeakGroup(
    string Publisher,
    string Field,
    int    Subscribers,
    bool   IsStaticPublisher = false,
    bool   HasStaticSubs = false,
    int    DuplicateCount = 0,
    long   RetainedBytes = 0,
    int    LambdaCount = 0,
    int    InstanceCount = 0,
    IReadOnlyList<EventSubscriberInfo>? AllSubs = null);

/// <summary>One subscriber entry from a delegate invocation list.</summary>
public sealed record EventSubscriberInfo(
    string TargetType,
    string MethodName,
    long   Size,
    bool   IsStaticRooted,
    bool   IsLambda,
    ulong  TargetAddr = 0);
