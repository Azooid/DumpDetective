namespace DumpDetective.Core.Interfaces;

public enum AlertLevel { Info, Warning, Critical }

/// <summary>
/// Format-agnostic output abstraction implemented by every sink:
/// <c>ConsoleSink</c>, <c>HtmlSink</c>, <c>MarkdownSink</c>, <c>TextSink</c>,
/// <c>JsonSink</c>, <c>CaptureSink</c>.
/// </summary>
public interface IRenderSink : IDisposable
{
    void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null);

    /// <summary>
    /// Begins a new named section.
    /// <paramref name="sectionKey"/> is a stable identifier (e.g. <c>"heap-fragmentation"</c>)
    /// used by <c>HtmlSink</c> to associate tooltip metadata without relying on title-string matching.
    /// All other sinks ignore <paramref name="sectionKey"/>.
    /// </summary>
    void Section(string title, string? sectionKey = null);

    void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null);
    void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null);
    void Alert(AlertLevel level, string title, string? detail = null, string? advice = null);
    void Text(string line);
    void BlankLine();
    void Reference(string label, string url);
    void BeginDetails(string title, bool open = false);
    void EndDetails();

    /// <summary>
    /// Emits a structured "explain" block that answers What / Why / Impact / Action.
    /// HTML renders as a styled card; other sinks render as plain text paragraphs.
    /// All parameters are optional — pass only the ones relevant to the section.
    /// </summary>
    void Explain(string? what, string? why = null, string[]? bullets = null,
                 string? impact = null, string? action = null);

    bool    IsFile   { get; }
    string? FilePath { get; }
}
