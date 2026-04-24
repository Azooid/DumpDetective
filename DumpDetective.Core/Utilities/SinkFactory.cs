using DumpDetective.Core.Interfaces;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Creates <see cref="IRenderSink"/> instances from output paths.
/// The actual factory is injected at startup by <c>DumpDetective.Reporting</c>
/// to avoid a circular dependency between Core and Reporting.
/// </summary>
public static class SinkFactory
{
    private static Func<string?, IRenderSink>? _factory;

    /// <summary>
    /// Called once at application startup (by <c>Program.cs</c>) to register the
    /// Reporting-layer factory before any commands run.
    /// </summary>
    public static void Register(Func<string?, IRenderSink> factory) => _factory = factory;

    /// <summary>Creates an <see cref="IRenderSink"/> for the given output path.</summary>
    public static IRenderSink Create(string? outputPath)
    {
        if (_factory is null)
            throw new InvalidOperationException(
                "SinkFactory has not been initialised. Call SinkFactory.Register() at startup.");
        return _factory(outputPath);
    }

    /// <summary>
    /// Creates a single <see cref="IRenderSink"/> for one path, or a <see cref="TeeRenderSink"/>
    /// that fans out to all paths when more than one is supplied.
    /// An empty or null list falls back to a console sink.
    /// </summary>
    public static IRenderSink CreateMulti(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return Create(null);
        if (paths.Count == 1) return Create(paths[0]);
        var sinks = new IRenderSink[paths.Count];
        for (int i = 0; i < paths.Count; i++) sinks[i] = Create(paths[i]);
        return new TeeRenderSink(sinks);
    }
}
