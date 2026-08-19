namespace DumpDetective.Analysis.Memory;

/// <summary>
/// Nullable wrapper for <see cref="DomTreeCache"/> so the result of
/// "try to load a dominator index" can be stored on
/// <c>DumpContext.GetOrCreateAnalysis&lt;T&gt;</c> (which requires a non-null class instance).
/// </summary>
public sealed class DomTreeCacheBox
{
    public readonly DomTreeCache? Cache;
    public DomTreeCacheBox(DomTreeCache? cache) => Cache = cache;
}
