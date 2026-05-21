using DumpDetective.Core.Runtime;

namespace DumpDetective.Core.Interfaces;

/// <summary>
/// Optional extension for commands that want to contribute custom
/// <see cref="IHeapObjectConsumer"/> instances to the shared full-analysis heap walk.
/// This lets built-in commands and plugins populate their own analysis caches during
/// the single walk instead of triggering a second enumeration later in <c>BuildReport</c>.
/// </summary>
public interface ICommandHeapContributor
{
    /// <summary>
    /// Creates the consumers that should participate in the shared heap walk.
    /// Returned instances must be ready to receive <see cref="IHeapObjectConsumer.Consume"/>
    /// calls immediately.
    /// </summary>
    IReadOnlyList<IHeapObjectConsumer> CreateHeapConsumers();

    /// <summary>
    /// Publishes the accumulated consumer results into <paramref name="ctx"/>
    /// after the shared heap walk completes.
    /// Typical implementations call <c>ctx.SetAnalysis(...)</c> or
    /// <c>ctx.PreloadAnalysis(...)</c>.
    /// </summary>
    void PublishResults(DumpContext ctx);
}