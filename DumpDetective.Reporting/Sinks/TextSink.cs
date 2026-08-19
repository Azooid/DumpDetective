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

    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CallTreeNode> roots,
                         string? caption = null, int topN = 20)
    {
        if (caption is not null) _w.WriteLine($"  {caption}");
        int shown = 0;
        RenderCtNodes(roots, 0, topN, ref shown);
    }

    void RenderCtNodes(IReadOnlyList<DumpDetective.Core.Models.CallTreeNode> nodes, int depth, int topN, ref int shown)
    {
        foreach (var n in nodes)
        {
            if (shown >= topN) return;
            shown++;
            string indent = new string(' ', 4 + depth * 2);
            _w.WriteLine($"{indent}{n.InclusivePct,5:F1}%  {n.Method}  [{n.Module}]");
            if (n.Children is { Count: > 0 })
                RenderCtNodes(n.Children, depth + 1, topN, ref shown);
        }
    }

    public void DomTree(IReadOnlyList<DumpDetective.Core.Models.DomRetainerNode> roots,
                        long totalHeapBytes, string? caption = null, int topN = 20)
    {
        if (caption is not null) _w.WriteLine($"  {caption}");
        int shown = 0;
        RenderDomNodes(roots, 0, topN, ref shown);
    }

    void RenderDomNodes(IReadOnlyList<DumpDetective.Core.Models.DomRetainerNode> nodes, int depth, int topN, ref int shown)
    {
        foreach (var n in nodes)
        {
            if (shown >= topN) return;
            shown++;
            string indent = new string(' ', 4 + depth * 2);
            _w.WriteLine($"{indent}{n.RetainedPct,5:F1}%  {DumpDetective.Core.Utilities.DumpHelpers.FormatSize(n.RetainedBytes)}  ×{n.InstanceCount:N0}  {n.TypeName}");
            if (n.Children is { Count: > 0 })
                RenderDomNodes(n.Children, depth + 1, topN, ref shown);
        }
    }

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
    {
        if (what   is not null) _w.WriteLine($"  [CONTEXT] {what}");
        if (why    is not null) _w.WriteLine($"  [WHY]     {why}");
        if (impact is not null) _w.WriteLine($"  [IMPACT]  {impact}");
        if (bullets is not null)
            foreach (var b in bullets) _w.WriteLine($"    • {b}");
        if (action is not null) _w.WriteLine($"  [ACTION]  {action}");
        _w.WriteLine();
    }

    public void MultiSparkline(
        IReadOnlyList<(string Label, IReadOnlyList<double> Values, string? Unit)> series,
        string? caption = null, string? valueMode = null)
    {
        if (caption is not null) _w.WriteLine($"  {caption}");
        foreach (var (label, values, unit) in series)
        {
            if (values.Count == 0) continue;
            string u = unit ?? "";
            _w.WriteLine($"  {label,-36}  min {values.Min():F1}{u}  avg {values.Average():F1}{u}  max {values.Max():F1}{u}");
        }
        _w.WriteLine();
    }

    public void CompareBar(
        IReadOnlyList<(string Label, double ValueA, double ValueB)> items,
        string? labelA = null, string? labelB = null,
        string? unit = null, string? caption = null, string? valueMode = null)
    {
        if (caption is not null) _w.WriteLine($"  {caption}");
        bool sizeMode = string.Equals(valueMode, "size", StringComparison.Ordinal);
        string Fmt(double v) => sizeMode
            ? DumpDetective.Core.Utilities.DumpHelpers.FormatSize((long)v)
            : $"{v:F1}{unit ?? ""}";
        string colA = (labelA ?? "A").PadLeft(12);
        string colB = (labelB ?? "B").PadLeft(12);
        _w.WriteLine($"  {"":44}  {colA}  {colB}");
        _w.WriteLine($"  {new string('-', 70)}");
        foreach (var (lbl, va, vb) in items)
            _w.WriteLine($"  {lbl,-44}  {Fmt(va),12}  {Fmt(vb),12}");
        _w.WriteLine();
    }

    public void Dispose() => _w.Dispose();
}
