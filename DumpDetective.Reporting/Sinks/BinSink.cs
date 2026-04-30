using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Json;
using System.IO.Compression;
using System.Text.Json;

namespace DumpDetective.Reporting.Sinks;

/// <summary>
/// Captures all rendered output into a Brotli-compressed JSON binary file (.bin).
/// The file is non-human-readable and highly compressed.
/// Re-render later with: <c>DumpDetective render report.bin --output report.html</c>
/// </summary>
public sealed class BinSink : IRenderSink
{
    private readonly string      _path;
    private readonly CaptureSink _capture = new();

    public bool    IsFile   => true;
    public string? FilePath => _path;

    public BinSink(string path) => _path = path;

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
    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CommandData.CpuCallNode> roots,
                         string? caption = null, int topN = 20)
        => _capture.CallTree(roots, caption, topN);
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

        using var fs      = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var brotli  = new BrotliStream(fs, CompressionLevel.SmallestSize, leaveOpen: false);
        using var writer  = new Utf8JsonWriter(brotli, new JsonWriterOptions { Indented = false });
        JsonSerializer.Serialize(writer, envelope, CoreJsonContext.Default.DumpReportEnvelope);
    }
}
