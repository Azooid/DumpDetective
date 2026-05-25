namespace DumpDetective.Core.Tracing;

/// <summary>
/// Optional extension of <see cref="ITraceEventClassifier"/> that declares which
/// ETW / EventPipe provider names this classifier is responsible for.
///
/// <para>Implementing this interface lets <c>EventNormalizer</c> build a provider-keyed
/// dispatch table (Phase 3 optimization): events from a known provider are routed only to
/// classifiers that declared ownership of that provider, skipping irrelevant ones.
/// Classifiers that do not implement this interface are tried for every event.</para>
///
/// <para>Provider matching is <b>OrdinalIgnoreCase</b>.  An empty <see cref="Providers"/>
/// list is equivalent to not implementing this interface — the classifier is unscoped.</para>
/// </summary>
public interface IProviderScopedClassifier : ITraceEventClassifier
{
    /// <summary>
    /// ETW / EventPipe provider names this classifier fully owns.
    /// </summary>
    IReadOnlyList<string> Providers { get; }
}
