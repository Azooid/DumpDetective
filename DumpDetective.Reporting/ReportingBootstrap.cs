using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Sinks;

namespace DumpDetective.Reporting;

/// <summary>
/// Called once at application startup to wire up the Reporting layer's
/// <see cref="IRenderSink"/> factory into <see cref="SinkFactory"/>,
/// and the default <see cref="ICommand.BuildReport"/> implementation into
/// <see cref="CommandBase.ReportDocBuilder"/>.
/// This avoids a circular reference between Core and Reporting.
/// </summary>
public static class ReportingBootstrap
{
    public static void Register()
    {
        SinkFactory.Register(outputPath => outputPath switch
        {
            null                                                                               => new ConsoleSink(),
            { } p when p.Equals("console", StringComparison.OrdinalIgnoreCase)               => new ConsoleSink(),
            // { } p when p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)       => new HtmlSink(p),
            { } p when p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)               => new HtmlSinkV2(p),
            { } p when p.EndsWith(".md",   StringComparison.OrdinalIgnoreCase)               => new MarkdownSink(p),
            { } p when p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)               => new JsonSink(p),
            { } p when p.EndsWith(".bin",  StringComparison.OrdinalIgnoreCase)               => new BinSink(p),
            { } p                                                                             => new TextSink(p),
        });

        CommandBase.ReportDocBuilder = static (cmd, ctx) =>
        {
            var cap = new CaptureSink();
            cmd.Render(ctx, cap);
            return cap.GetDoc();
        };
    }
}
