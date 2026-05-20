namespace DumpDetective.Core.Interfaces;

/// <summary>
/// Optional extension for commands that require one or more analysis cache entries
/// to remain available for the duration of a batch run.
/// The orchestrator pins these types before parallel sub-reports start and unpins
/// them after the batch completes.
/// </summary>
public interface ICommandCachePin
{
    /// <summary>
    /// Analysis-cache entry types that must not be released while this command is active.
    /// The type should match the <c>T</c> used with <c>DumpContext.GetOrCreateAnalysis&lt;T&gt;</c>,
    /// <c>SetAnalysis&lt;T&gt;</c>, or <c>PreloadAnalysis&lt;T&gt;</c>.
    /// </summary>
    IReadOnlyList<Type> PinnedCacheTypes { get; }
}