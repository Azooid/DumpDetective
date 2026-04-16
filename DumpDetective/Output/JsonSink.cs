using DumpDetective.Models;
using System.Text.Json;

namespace DumpDetective.Output;

/// <summary>
/// Captures all rendered output into a <see cref="DumpReportEnvelope"/> JSON file.
/// Uses <see cref="CaptureSink"/> internally; serialises via the AOT source-gen context on Dispose.
/// <para>
/// Top-level shape:
/// <code>
/// {
///   "format":      "report",
///   "generatedAt": "ISO-8601",
///   "title":       "...",
///   "subtitle":    "...",
///   "doc": {
///     "chapters": [
///       {
///         "title": "...", "subtitle": "...", "navLevel": 1,
///         "sections": [
///           {
///             "title": "...",
///             "elements": [
///               { "type": "keyValues", "title": "...", "pairs": [{"key":"K","value":"V"}] },
///               { "type": "table",  "caption":"...", "headers":["H1"], "rows":[["v1"]] },
///               { "type": "alert",  "level":"info|warning|critical", "title":"...", "detail":"...", "advice":"..." },
///               { "type": "text",   "content":"..." },
///               { "type": "details","title":"...","open":true, "elements":[...] }
///             ]
///           }
///         ]
///       }
///     ]
///   }
/// }
/// </code>
/// </para>
/// Re-render later with: <c>DumpDetective render report.json --output report.html</c>
/// </summary>
internal sealed class JsonSink : IRenderSink
{
    readonly string      _path;
    readonly CaptureSink _capture = new();

    public bool    IsFile   => true;
    public string? FilePath => _path;

    public JsonSink(string path) => _path = path;

    public void Header(string title, string? subtitle = null, int navLevel = 0)                      => _capture.Header(title, subtitle, navLevel);
    public void Section(string title)                                              => _capture.Section(title);
    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null) => _capture.KeyValues(pairs, title);
    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)    => _capture.Table(headers, rows, caption);
    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null) => _capture.Alert(level, title, detail, advice);
    public void Text(string line)                                                  => _capture.Text(line);
    public void Reference(string label, string url)                                => _capture.Reference(label, url);
    public void BlankLine()                                                        { /* not meaningful in JSON */ }
    public void BeginDetails(string title, bool open = false)                      => _capture.BeginDetails(title, open);
    public void EndDetails()                                                       => _capture.EndDetails();

    public void Dispose()
    {
        var doc      = _capture.GetDoc();
        var envelope = new DumpReportEnvelope
        {
            GeneratedAt = DateTime.UtcNow.ToString("o"),
            Title       = doc.Chapters.FirstOrDefault()?.Title ?? string.Empty,
            Subtitle    = doc.Chapters.FirstOrDefault()?.Subtitle,
            Doc         = doc,
        };
        using var fs     = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, envelope, RawTrendContext.Default.DumpReportEnvelope);
    }
}
