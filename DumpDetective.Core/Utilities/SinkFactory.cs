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
}
