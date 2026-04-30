using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Fan-out <see cref="IRenderSink"/> that forwards every call to all inner sinks.
/// Used to write the same report to multiple output formats in a single pass.
/// </summary>
public sealed class TeeRenderSink : IRenderSink
{
    private readonly IRenderSink[] _sinks;

    public TeeRenderSink(IRenderSink[] sinks) => _sinks = sinks;

    public bool    IsFile   => _sinks.Any(s => s.IsFile);
    public string? FilePath => _sinks.FirstOrDefault(s => s.IsFile)?.FilePath;

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
    { foreach (var s in _sinks) s.Header(title, subtitle, navLevel, commandName); }

    public void Section(string title, string? sectionKey = null)
    { foreach (var s in _sinks) s.Section(title, sectionKey); }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    { foreach (var s in _sinks) s.KeyValues(pairs, title); }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    { foreach (var s in _sinks) s.Table(headers, rows, caption); }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    { foreach (var s in _sinks) s.Alert(level, title, detail, advice); }

    public void Text(string line)
    { foreach (var s in _sinks) s.Text(line); }

    public void BlankLine()
    { foreach (var s in _sinks) s.BlankLine(); }

    public void Reference(string label, string url)
    { foreach (var s in _sinks) s.Reference(label, url); }

    public void BeginDetails(string title, bool open = false)
    { foreach (var s in _sinks) s.BeginDetails(title, open); }

    public void EndDetails()
    { foreach (var s in _sinks) s.EndDetails(); }

    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CommandData.CpuCallNode> roots,
                         string? caption = null, int topN = 20)
    { foreach (var s in _sinks) s.CallTree(roots, caption, topN); }

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
    { foreach (var s in _sinks) s.Explain(what, why, bullets, impact, action); }

    public void Gauges(IReadOnlyList<(string Label, double Value, string Unit)> items, double barMax = 100.0)
    { foreach (var s in _sinks) s.Gauges(items, barMax); }

    public void DonutChart(IReadOnlyList<(string Label, double Value)> segments,
        string? caption = null, string? centerText = null)
    { foreach (var s in _sinks) s.DonutChart(segments, caption, centerText); }

    public void StackedBar(IReadOnlyList<(string Label, double Value)> segments,
        string? unit = null, string? caption = null, string? valueMode = null)
    { foreach (var s in _sinks) s.StackedBar(segments, unit, caption, valueMode); }

    public void Sparkline(IReadOnlyList<double> values, string? caption = null, string? unit = null,
        string? valueMode = null)
    { foreach (var s in _sinks) s.Sparkline(values, caption, unit, valueMode); }

    public void Dispose()
    { foreach (var s in _sinks) s.Dispose(); }
}
