namespace DumpDetective.Core.Runtime;

/// <summary>
/// Heap-walk-derived map from managed thread ID to thread name.
/// Stored in <see cref="DumpContext.GetOrCreateAnalysis{T}"/> so that
/// <c>ThreadAnalysisAnalyzer</c> and <c>DeadlockAnalyzer</c> share a single
/// heap walk even when executing in parallel.
/// </summary>
public sealed class ThreadNameMap : Dictionary<int, string> { }
