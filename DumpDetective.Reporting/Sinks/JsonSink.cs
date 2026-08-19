using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Json;
using System.Text.Json;

namespace DumpDetective.Reporting.Sinks;

/// <summary>
/// Captures all rendered output into a <see cref="DumpReportEnvelope"/> JSON file.
/// Delegates to <see cref="CaptureSink"/> internally; serialises via AOT source-gen on Dispose.
/// Re-render later with: <c>DumpDetective render report.json --output report.html</c>
/// </summary>
public sealed class JsonSink : IRenderSink
{
    private readonly string      _path;
    private readonly CaptureSink _capture = new();

    public bool    IsFile   => true;
    public string? FilePath => _path;

    public JsonSink(string path) => _path = path;

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
        => _capture.Header(title, subtitle, navLevel);
    public void Section(string title, string? sectionKey = null)
        => _capture.Section(title, sectionKey);
    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
        => _capture.KeyValues(pairs, title);
    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
        => _capture.Table(headers, rows, caption);
    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
        => _capture.Alert(level, title, detail, advice);
    public void Text(string line)    => _capture.Text(line);
    public void Reference(string label, string url) => _capture.Reference(label, url);
    public void BlankLine()          { }
    public void BeginDetails(string title, bool open = false) => _capture.BeginDetails(title, open);
    public void EndDetails()         => _capture.EndDetails();
    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CallTreeNode> roots,
                         string? caption = null, int topN = 20)
        => _capture.CallTree(roots, caption, topN);
    public void DomTree(IReadOnlyList<DumpDetective.Core.Models.DomRetainerNode> roots,
                        long totalHeapBytes, string? caption = null, int topN = 20)
        => _capture.DomTree(roots, totalHeapBytes, caption, topN);
    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
        => _capture.Explain(what, why, bullets, impact, action);

    public void Gauges(IReadOnlyList<(string Label, double Value, string Unit)> items, double barMax = 100.0)
        => _capture.Gauges(items, barMax);
    public void DonutChart(IReadOnlyList<(string Label, double Value)> segments,
        string? caption = null, string? centerText = null)
        => _capture.DonutChart(segments, caption, centerText);
    public void StackedBar(IReadOnlyList<(string Label, double Value)> segments,
        string? unit = null, string? caption = null, string? valueMode = null)
        => _capture.StackedBar(segments, unit, caption, valueMode);
    public void Sparkline(IReadOnlyList<double> values, string? caption = null, string? unit = null,
        string? valueMode = null)
        => _capture.Sparkline(values, caption, unit, valueMode);
    public void MultiSparkline(
        IReadOnlyList<(string Label, IReadOnlyList<double> Values, string? Unit)> series,
        string? caption = null, string? valueMode = null)
        => _capture.MultiSparkline(series, caption, valueMode);
    public void CompareBar(
        IReadOnlyList<(string Label, double ValueA, double ValueB)> items,
        string? labelA = null, string? labelB = null,
        string? unit = null, string? caption = null, string? valueMode = null)
        => _capture.CompareBar(items, labelA, labelB, unit, caption, valueMode);

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
        JsonSerializer.Serialize(writer, envelope, CoreJsonContext.Default.DumpReportEnvelope);
    }
}
