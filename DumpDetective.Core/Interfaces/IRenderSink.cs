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
    /// Renders a call-tree hierarchy (e.g. CPU hot-path or full call tree).
    /// <c>HtmlSink</c> renders an interactive collapsible flame-style tree.
    /// All other sinks fall back to an indented text representation.
    /// </summary>
    void CallTree(IReadOnlyList<DumpDetective.Core.Models.CallTreeNode> roots,
                  string? caption = null, int topN = 20);

    /// <summary>
    /// Renders labeled metrics as CSS progress-bar gauges.
    /// Each item has a label, a value, and an optional bar fill percentage (0–100).
    /// If <paramref name="barMax"/> is &gt; 0 the bar is filled to <c>value/barMax×100</c>;
    /// values above <paramref name="barMax"/> fill the bar fully and show a ×N badge.
    /// <c>HtmlSink</c> renders the full gauge widget; all other sinks fall back to KeyValues.
    /// </summary>
    void Gauges(IReadOnlyList<(string Label, double Value, string Unit)> items,
                double barMax = 100.0)
    {
        // Default plain-text fallback — KeyValues with "value unit" strings
        KeyValues(items.Select(i => (i.Label, $"{i.Value:F1}{i.Unit}")).ToList());
    }

    /// <summary>
    /// Renders a donut (ring) chart from named segments.
    /// <paramref name="centerText"/> appears in the hole; use '\n' for two lines.
    /// HTML renders an interactive SVG donut; other sinks fall back to a KV table.
    /// </summary>
    void DonutChart(IReadOnlyList<(string Label, double Value)> segments,
                    string? caption = null, string? centerText = null)
    {
        double total = segments.Sum(s => s.Value);
        KeyValues(segments.Select(s =>
            (s.Label, $"{s.Value:F1}  ({(total > 0 ? s.Value / total * 100 : 0):F1}%)")).ToList(),
            caption);
    }

    /// <summary>
    /// Renders a 100 % stacked horizontal bar from named segments.
    /// HTML renders a coloured segmented bar + legend; other sinks fall back to a KV table.
    /// </summary>
    void StackedBar(IReadOnlyList<(string Label, double Value)> segments,
                    string? unit = null, string? caption = null,
                    string? valueMode = null)
    {
        double total = segments.Sum(s => s.Value);
        bool sizeMode  = string.Equals(valueMode, "size",  StringComparison.Ordinal);
        bool countMode = string.Equals(valueMode, "count", StringComparison.Ordinal);
        KeyValues(segments.Select(s => {
            string displayVal = sizeMode
                ? DumpDetective.Core.Utilities.DumpHelpers.FormatSize((long)s.Value)
                : countMode
                    ? ((long)s.Value).ToString("N0")
                    : $"{s.Value:F1}{unit ?? ""}";
            return (s.Label, $"{displayVal}  ({(total > 0 ? s.Value / total * 100 : 0):F1}%)");
        }).ToList(), caption);
    }

    /// <summary>
    /// Renders a compact inline sparkline time-series.
    /// HTML renders an SVG polyline with min/avg/max annotations; other sinks emit a text summary.
    /// </summary>
    void Sparkline(IReadOnlyList<double> values, string? caption = null, string? unit = null,
                   string? valueMode = null)
    {
        if (values.Count == 0) return;
        string u = unit ?? "";
        Text($"{caption ?? "Trend"}: min {values.Min():F1}{u}  avg {values.Average():F1}{u}  max {values.Max():F1}{u}");
    }

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
