namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Nullable wrapper for <see cref="BfsIndexCache"/> so the result of
/// "try to load a cache" can be stored on <c>DumpContext.GetOrCreateAnalysis&lt;T&gt;</c>
/// (which requires a non-null class instance).
/// </summary>
public sealed class BfsCacheBox
{
    /// <summary>The loaded cache, or <see langword="null"/> if no valid file existed.</summary>
    public readonly BfsIndexCache? Cache;

    public BfsCacheBox(BfsIndexCache? cache) => Cache = cache;
}
