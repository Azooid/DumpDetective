using DumpDetective.Core.Interfaces;

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

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
    { foreach (var s in _sinks) s.Explain(what, why, bullets, impact, action); }

    public void Dispose()
    { foreach (var s in _sinks) s.Dispose(); }
}
