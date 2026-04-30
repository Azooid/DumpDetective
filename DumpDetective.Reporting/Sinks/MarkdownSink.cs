using DumpDetective.Core.Interfaces;

namespace DumpDetective.Reporting.Sinks;

/// <summary>GFM Markdown output to a file.</summary>
public sealed class MarkdownSink : IRenderSink
{
    private readonly StreamWriter _w;
    public bool    IsFile   => true;
    public string? FilePath => (_w.BaseStream as FileStream)?.Name;

    public MarkdownSink(string path) => _w = new StreamWriter(path, append: false);

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
    {
        _w.WriteLine($"# {title}");
        if (subtitle is not null) _w.WriteLine($"*{subtitle}*");
        _w.WriteLine();
    }

    public void Section(string title, string? sectionKey = null)
    {
        _w.WriteLine();
        _w.WriteLine($"## {title}");
        _w.WriteLine();
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null) { _w.WriteLine($"### {title}"); _w.WriteLine(); }
        _w.WriteLine("| Key | Value |");
        _w.WriteLine("|-----|-------|");
        foreach (var (k, v) in pairs) _w.WriteLine($"| {E(k)} | {E(v)} |");
        _w.WriteLine();
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        if (caption is not null) { _w.WriteLine($"*{caption}*"); _w.WriteLine(); }
        _w.WriteLine("| " + string.Join(" | ", headers.Select(E)) + " |");
        _w.WriteLine("| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
        foreach (var row in rows)
        {
            var cells = Enumerable.Range(0, headers.Length)
                .Select(i => i < row.Length ? E(row[i]) : "");
            _w.WriteLine("| " + string.Join(" | ", cells) + " |");
        }
        _w.WriteLine();
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        string icon = level switch { AlertLevel.Critical => "🔴", AlertLevel.Warning => "🟡", _ => "🔵" };
        _w.WriteLine($"> {icon} **{level}: {E(title)}**");
        if (detail is not null) _w.WriteLine($"> {E(detail)}");
        if (advice  is not null) _w.WriteLine($"> → {E(advice)}");
        _w.WriteLine();
    }

    public void Text(string line)  => _w.WriteLine(line);
    public void BlankLine()        => _w.WriteLine();
    public void Reference(string label, string url)
        => _w.WriteLine($"> 📖 {E(label)}: [{E(url)}]({url})");
    public void BeginDetails(string title, bool open = false)
        { _w.WriteLine($"### {title}"); _w.WriteLine(); }
    public void EndDetails() => _w.WriteLine();

    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CommandData.CpuCallNode> roots,
                         string? caption = null, int topN = 20)
    {
        if (caption is not null) _w.WriteLine($"**{caption}**\n");
        _w.WriteLine("| Method | Module | Incl% | Excl% | Incl |");
        _w.WriteLine("|--------|--------|------:|------:|-----:|");
        int shown = 0;
        WriteNodes(roots, 0, topN, ref shown);
        _w.WriteLine();
    }

    void WriteNodes(IReadOnlyList<DumpDetective.Core.Models.CommandData.CpuCallNode> nodes, int depth, int topN, ref int shown)
    {
        foreach (var n in nodes)
        {
            if (shown >= topN) return;
            shown++;
            string indent = new string('·', depth * 2);
            _w.WriteLine($"| {indent}{E(n.Method)} | {E(n.Module)} | {n.InclusivePct:F1}% | {n.ExclusivePct:F1}% | {n.InclusiveSamples:N0} |");
            if (n.Children is { Count: > 0 })
                WriteNodes(n.Children, depth + 1, topN, ref shown);
        }
    }

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
    {
        _w.WriteLine("> **Context**");
        if (what   is not null) _w.WriteLine($"> **What:** {what}");
        if (why    is not null) _w.WriteLine($"> **Why it matters:** {why}");
        if (impact is not null) _w.WriteLine($"> **Impact:** {impact}");
        if (bullets is not null)
            foreach (var b in bullets) _w.WriteLine($"> - {b}");
        if (action is not null) _w.WriteLine($"> **Recommended action:** {action}");
        _w.WriteLine();
    }

    public void Dispose() => _w.Dispose();

    private static string E(string s) => s.Replace("|", "\\|").Replace("`", "'");
}
