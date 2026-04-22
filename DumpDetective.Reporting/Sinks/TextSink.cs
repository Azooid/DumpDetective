using DumpDetective.Core.Interfaces;

namespace DumpDetective.Reporting.Sinks;

/// <summary>Plain-text file output with ASCII-aligned tables.</summary>
public sealed class TextSink : IRenderSink
{
    private readonly StreamWriter _w;
    public bool    IsFile   => true;
    public string? FilePath => (_w.BaseStream as FileStream)?.Name;

    public TextSink(string path) => _w = new StreamWriter(path, append: false);

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
    {
        string bar = new('═', 80);
        _w.WriteLine(bar);
        _w.WriteLine($"  {title}");
        if (subtitle is not null) _w.WriteLine($"  {subtitle}");
        _w.WriteLine(bar);
        _w.WriteLine();
    }

    public void Section(string title, string? sectionKey = null)
    {
        _w.WriteLine();
        _w.WriteLine(new string('-', 80));
        _w.WriteLine($"  {title.ToUpperInvariant()}");
        _w.WriteLine(new string('-', 80));
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null) _w.WriteLine($"\n  {title}");
        int kw = pairs.Max(p => p.Key.Length);
        foreach (var (k, v) in pairs)
            _w.WriteLine($"  {k.PadRight(kw)}  {v}");
        _w.WriteLine();
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        if (rows.Count == 0) { _w.WriteLine("  (no data)"); return; }
        if (caption is not null) _w.WriteLine($"  {caption}");
        var widths = headers.Select((h, i) =>
            Math.Max(h.Length, rows.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        _w.WriteLine("  " + string.Join("   ", headers.Select((h, i) => h.PadRight(widths[i]))));
        _w.WriteLine("  " + string.Join("   ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            var cells = Enumerable.Range(0, headers.Length)
                .Select(i => (i < row.Length ? row[i] : "").PadRight(widths[i]));
            _w.WriteLine("  " + string.Join("   ", cells));
        }
        _w.WriteLine();
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        string tag = level switch { AlertLevel.Critical => "[CRITICAL]", AlertLevel.Warning => "[WARNING] ", _ => "[INFO]    " };
        _w.WriteLine($"  {tag}  {title}");
        if (detail is not null) _w.WriteLine($"            {detail}");
        if (advice  is not null) _w.WriteLine($"            → {advice}");
    }

    public void Text(string line)  => _w.WriteLine($"  {line}");
    public void BlankLine()        => _w.WriteLine();
    public void Reference(string label, string url) => _w.WriteLine($"  📖 {label}: {url}");
    public void BeginDetails(string title, bool open = false) => _w.WriteLine($"  ▸ {title}");
    public void EndDetails() => _w.WriteLine();
    public void Dispose()    => _w.Dispose();
}
