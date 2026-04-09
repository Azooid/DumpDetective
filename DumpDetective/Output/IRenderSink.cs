namespace DumpDetective.Output;

public enum AlertLevel { Info, Warning, Critical }

/// <summary>
/// Format-agnostic output abstraction used by every command.
/// Implementations: <see cref="ConsoleSink"/> (Spectre.Console),
/// <see cref="MarkdownSink"/>, <see cref="HtmlSink"/>, <see cref="TextSink"/>.
/// </summary>
public interface IRenderSink : IDisposable
{
    void Header(string title, string? subtitle = null);
    void Section(string title);
    void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null);
    void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null);
    void Alert(AlertLevel level, string title, string? detail = null, string? advice = null);
    void Text(string line);
    void BlankLine();
    /// <summary>Begins a collapsible group (accordion). Content follows until <see cref="EndDetails"/>.</summary>
    void BeginDetails(string title, bool open = false);
    void EndDetails();

    bool    IsFile   { get; }
    string? FilePath { get; }

    static IRenderSink Create(string? outputPath) => outputPath switch
    {
        null                                                                      => new ConsoleSink(),
        { } p when p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)       => new HtmlSink(p),
        { } p when p.EndsWith(".md",   StringComparison.OrdinalIgnoreCase)       => new MarkdownSink(p),
        { } p                                                                     => new TextSink(p),
    };
}
