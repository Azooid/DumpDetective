namespace DumpDetective.Output;

/// <summary>Self-contained HTML report output.</summary>
internal sealed class HtmlSink : IRenderSink
{
    readonly StreamWriter _w;
    bool _inSection;

    public bool    IsFile   => true;
    public string? FilePath => (_w.BaseStream as FileStream)?.Name;

    public HtmlSink(string path)
    {
        _w = new StreamWriter(path, append: false);
        WriteDocHeader();
    }

    // ── IRenderSink ───────────────────────────────────────────────────────────

    public void Header(string title, string? subtitle = null)
    {
        _w.WriteLine($"""
            <div class="hero">
              <h1>{H(title)}</h1>
              {(subtitle is not null ? $"<p class=\"sub\">{H(subtitle)}</p>" : "")}
            </div>
            """);
    }

    public void Section(string title)
    {
        if (_inSection) _w.WriteLine("</div>");
        _w.WriteLine($"<div class=\"card\"><h2>{H(title)}</h2>");
        _inSection = true;
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null) _w.WriteLine($"<h3>{H(title)}</h3>");
        _w.WriteLine("<table><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>");
        foreach (var (k, v) in pairs)
            _w.WriteLine($"<tr><td>{H(k)}</td><td>{H(v)}</td></tr>");
        _w.WriteLine("</tbody></table>");
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        if (caption is not null) _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");
        _w.WriteLine("<table><thead><tr>");
        foreach (var h in headers) _w.Write($"<th>{H(h)}</th>");
        _w.WriteLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            _w.Write("<tr>");
            for (int i = 0; i < headers.Length; i++)
                _w.Write($"<td>{H(i < row.Length ? row[i] : "")}</td>");
            _w.WriteLine("</tr>");
        }
        _w.WriteLine("</tbody></table>");
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        string cls = level switch { AlertLevel.Critical => "alert-crit", AlertLevel.Warning => "alert-warn", _ => "alert-info" };
        string icon = level switch { AlertLevel.Critical => "✗", AlertLevel.Warning => "⚠", _ => "ℹ" };
        _w.WriteLine($"<div class=\"alert {cls}\"><strong>{icon} {level}:</strong> {H(title)}");
        if (detail is not null) _w.WriteLine($"<br><span class=\"detail\">{H(detail)}</span>");
        if (advice  is not null) _w.WriteLine($"<br><span class=\"advice\">→ {H(advice)}</span>");
        _w.WriteLine("</div>");
    }

    public void Text(string line)  => _w.WriteLine($"<p>{H(line)}</p>");
    public void BlankLine()        => _w.WriteLine("<br>");

    public void Dispose()
    {
        if (_inSection) _w.WriteLine("</div>");
        _w.WriteLine("</main></body></html>");
        _w.Dispose();
    }

    // ── HTML helpers ──────────────────────────────────────────────────────────

    static string H(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    void WriteDocHeader() => _w.Write($$"""
        <!DOCTYPE html><html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Dump Detective Report</title>
        <style>
          *{box-sizing:border-box;margin:0;padding:0}
          body{font-family:system-ui,-apple-system,sans-serif;background:#f4f6f9;color:#222;font-size:14px;line-height:1.5}
          .hero{background:linear-gradient(135deg,#1a1a2e,#16213e);color:#fff;padding:2rem;margin-bottom:1rem}
          .hero h1{font-size:1.6rem;margin-bottom:.4rem}
          .hero .sub{opacity:.7;font-size:.9rem;font-family:monospace}
          main{max-width:1200px;margin:0 auto;padding:.75rem 1rem}
          .card{background:#fff;border-radius:8px;padding:1.25rem;margin:.75rem 0;box-shadow:0 1px 4px rgba(0,0,0,.08)}
          h2{font-size:1.1rem;margin-bottom:.75rem;color:#1a1a2e}
          h3{font-size:.95rem;margin:.6rem 0 .4rem;color:#333}
          table{width:100%;border-collapse:collapse;font-size:13px;margin:.5rem 0}
          th{background:#f0f4f8;padding:.4rem .7rem;text-align:left;font-weight:600;border-bottom:2px solid #dde3ea}
          td{padding:.35rem .7rem;border-bottom:1px solid #eef0f3}
          tr:hover td{background:#fafbfc}
          p.caption{font-size:12px;color:#888;margin-bottom:.3rem}
          .alert{padding:.6rem 1rem;border-radius:5px;margin:.4rem 0;font-size:13px}
          .alert-crit{background:#fef2f2;border-left:4px solid #ef4444}
          .alert-warn{background:#fffbeb;border-left:4px solid #f59e0b}
          .alert-info{background:#eff6ff;border-left:4px solid #3b82f6}
          .detail{color:#555;font-size:12px}
          .advice{color:#16a34a;font-size:12px}
        </style></head>
        <body><main>
        """);
}
