namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>PinnedObjectsAnalyzer</c>.</summary>
public sealed record PinnedObjectsData(IReadOnlyList<PinnedItem> Items);

public sealed record PinnedItem(
    string TypeName,
    ulong  Addr,
    long   Size,
    string Gen,
    bool   IsAsyncPinned);
