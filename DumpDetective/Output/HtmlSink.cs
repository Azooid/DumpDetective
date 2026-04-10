using System.Text;
using DumpDetective.Core;

namespace DumpDetective.Output;

/// <summary>
/// Self-contained HTML report output — fully offline, no external dependencies.
/// Optimised for large combined reports (full analyze / trend-full):
///   • Auto-built sticky sidebar navigation from chapter headers
///   • Collapsible card sections (click h2 heading to toggle)
///   • Client-side table sort (click any column header)
///   • Live table search / filter input per section
///   • Virtual scroll for tables with > 200 rows (only renders visible rows)
///   • Back-to-top button
///   • Print-friendly styles
///   • Score badges, alert colour coding, responsive layout
/// </summary>
internal sealed class HtmlSink : IRenderSink
{
    readonly StreamWriter _w;
    bool _inSection;
    bool _chapterBodyOpen;  // true when a <div class="chapter-body"> is open
    int  _sectionSeq;      // unique id per section card
    int  _tableSeq;        // unique id per table (for sort/search)
    int  _chapterSeq;      // unique id per hero (chapter)

    public bool    IsFile   => true;
    public string? FilePath => (_w.BaseStream as FileStream)?.Name;

    public HtmlSink(string path)
    {
        _w = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteDocHeader();
    }

    // ── IRenderSink ───────────────────────────────────────────────────────────

    public void Header(string title, string? subtitle = null)
    {
        CloseSection();
        int id = ++_chapterSeq;
        string meta = string.Empty;
        if (subtitle is not null)
        {
            var parts = subtitle
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p =>
                {
                    // Score chips get colour-coded badge
                    string cls = "chip";
                    if (p.StartsWith("Score:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Contains("/100"))
                        {
                            var numStr = p.Replace("Score:", "").Trim().Split('/')[0].Trim();
                            if (int.TryParse(numStr, out int sc))
                                cls = sc >= 70 ? "chip chip-ok" : sc >= 40 ? "chip chip-warn" : "chip chip-crit";
                        }
                    }
                    return $"<span class=\"{cls}\">{H(p)}</span>";
                });
            meta = $"<div class=\"hero-meta\">{string.Join(string.Empty, parts)}</div>";
        }

        // Close the previous chapter-body before opening a new hero.
        // Without this every chapter-body nests inside the first, breaking the nav.
        if (_chapterBodyOpen)
            _w.WriteLine("</div> <!-- /chapter-body -->");

        // SuppressVerbose is true while RenderEmbeddedReports runs sub-commands
        // → those headers are secondary (collapsed in nav), level-1 otherwise.
        string navLevel = CommandBase.SuppressVerbose ? "2" : "1";
        // For level-2 (sub-command) headers strip the "Dump Detective — " prefix that
        // every command includes in its title — it's redundant inside a parent chapter.
        string displayTitle = navLevel == "2"
            ? System.Text.RegularExpressions.Regex.Replace(title, @"^Dump Detective\s*[—\-]\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : title;

        _w.WriteLine($"""
            <div class="hero" id="ch{id}" data-nav-level="{navLevel}">
              <h1 class="hero-title">{H(displayTitle)}</h1>
              {meta}
            </div>
            <div class="chapter-body" id="chb{id}">
            """);
        _chapterBodyOpen = true;
    }

    public void Section(string title)
    {
        CloseSection();
        int id = ++_sectionSeq;
        _w.WriteLine($"""
            <div class="card" id="s{id}">
              <h2 class="card-title" onclick="toggleCard('s{id}')" title="Click to collapse/expand">
                <span class="card-arrow">▾</span>{H(title)}
              </h2>
              <div class="card-body">
            """);
        _inSection = true;
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null) _w.WriteLine($"<h3>{H(title)}</h3>");
        _w.WriteLine("<div class=\"kv-grid\">");
        foreach (var (k, v) in pairs)
        {
            // Try to detect score lines for colour badge
            string valHtml = H(v);
            if (k.Contains("score", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("health", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = v.Split('/')[0].Trim();
                if (int.TryParse(numStr, out int sc))
                {
                    string badgeCls = sc >= 70 ? "badge-ok" : sc >= 40 ? "badge-warn" : "badge-crit";
                    valHtml = $"<span class=\"score-badge {badgeCls}\">{H(v)}</span>";
                }
            }
            _w.WriteLine($"<div class=\"kv-row\"><span class=\"kv-key\">{H(k)}</span><span class=\"kv-val\">{valHtml}</span></div>");
        }
        _w.WriteLine("</div>");
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        int tid = ++_tableSeq;
        bool large = rows.Count > 200;

        if (caption is not null) _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");

        // Search input
        _w.WriteLine($"""
            <div class="table-toolbar">
              <input class="tbl-search" id="ts{tid}" placeholder="Filter rows…" oninput="filterTable({tid})" autocomplete="off">
              <span class="row-count" id="rc{tid}">{rows.Count:N0} rows</span>
            </div>
            """);

        _w.WriteLine($"<div class=\"table-wrap{(large ? " tbl-large" : "")}\" id=\"tw{tid}\">");
        _w.WriteLine($"<table id=\"t{tid}\" class=\"data-table\">");
        _w.WriteLine("<thead><tr>");
        for (int i = 0; i < headers.Length; i++)
            _w.Write($"<th onclick=\"sortTable({tid},{i})\" title=\"Sort by {H(headers[i])}\">{H(headers[i])}<span class=\"sort-icon\">⇅</span></th>");
        _w.WriteLine("</tr></thead>");
        _w.WriteLine("<tbody>");

        // For large tables write all rows but mark overflow rows as lazy (display:none initially)
        // JS virtual scroll engine will show/hide based on scroll position
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            string rowClass = large && r >= 200 ? " class=\"vr\"" : "";
            _w.Write($"<tr{rowClass}>");
            for (int i = 0; i < headers.Length; i++)
            {
                string cell = i < row.Length ? row[i] : string.Empty;
                // Color-code cells containing ↑↑ / ↑ / ↓ trend arrows
                string cellCls = "";
                if (cell is "↑↑" or "↑↑ ↑↑") cellCls = " class=\"trend-up2\"";
                else if (cell is "↑" or "↑ ↑")  cellCls = " class=\"trend-up\"";
                else if (cell.StartsWith("↓"))    cellCls = " class=\"trend-dn\"";
                else if (cell.Length > 80)        cellCls = " class=\"long-text\"";
                // Colour-code alert-level cells
                else if (cell is "Critical")      cellCls = " class=\"sev-crit\"";
                else if (cell is "Warning")       cellCls = " class=\"sev-warn\"";
                else if (cell is "Info")          cellCls = " class=\"sev-info\"";
                _w.Write($"<td{cellCls}>{H(cell)}</td>");
            }
            _w.WriteLine("</tr>");
        }

        _w.WriteLine("</tbody></table></div>");

        if (large)
            _w.WriteLine($"<p class=\"caption\">⚡ {rows.Count:N0} rows — showing first 200; type in the filter box to search all rows.</p>");
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        string cls  = level switch { AlertLevel.Critical => "alert-crit", AlertLevel.Warning => "alert-warn", _ => "alert-info" };
        string icon = level switch { AlertLevel.Critical => "✗", AlertLevel.Warning => "⚠", _ => "ℹ" };
        _w.Write($"<div class=\"alert {cls}\"><div class=\"alert-title\"><span class=\"alert-icon\">{icon}</span>{H(title)}</div>");
        if (detail is not null) _w.Write($"<div class=\"alert-detail\">{H(detail)}</div>");
        if (advice  is not null) _w.Write($"<div class=\"alert-advice\"><span class=\"advice-label\">💡 Recommendation</span>{H(advice)}</div>");
        _w.WriteLine("</div>");
    }

    public void Text(string line)  => _w.WriteLine($"<p class=\"body-text\">{H(line)}</p>");
    public void BlankLine()        => _w.WriteLine("<div class=\"spacer\"></div>");

    public void BeginDetails(string title, bool open = false)
    {
        string openAttr = open ? " open" : string.Empty;
        _w.WriteLine($"<details{openAttr}><summary class=\"det-sum\">{H(title)}</summary><div class=\"details-body\">");
    }

    public void EndDetails() => _w.WriteLine("</div></details>");

    public void Dispose()
    {
        CloseSection();
        if (_chapterBodyOpen)
            _w.WriteLine("</div> <!-- /chapter-body -->");
        _w.WriteLine(Footer);
        _w.Dispose();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    void CloseSection()
    {
        if (_inSection)
        {
            _w.WriteLine("</div></div>"); // .card-body + .card
            _inSection = false;
        }
    }

    static string H(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    // ── Document head + CSS + skeleton ───────────────────────────────────────

    void WriteDocHeader() => _w.Write(DocHeader);

    const string DocHeader = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Dump Detective Report</title>
        <style>
        /* ── Reset / Base ─────────────────────────────────────────────── */
        *{box-sizing:border-box;margin:0;padding:0}
        html{scroll-behavior:smooth}
        body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;background:#f0f4f8;color:#1a202c;font-size:14px;line-height:1.55;display:flex;min-height:100vh}

        /* ── Sidebar nav ──────────────────────────────────────────────── */
        #sidebar{position:sticky;top:0;height:100vh;width:200px;min-width:180px;overflow-y:auto;background:#1e293b;color:#cbd5e1;padding:.75rem .6rem;flex-shrink:0;font-size:12px}
        #sidebar h3{color:#94a3b8;text-transform:uppercase;letter-spacing:.08em;font-size:10px;margin-bottom:.5rem;padding-bottom:.3rem;border-bottom:1px solid #334155}
        #sidebar a{display:block;padding:.25rem .45rem;color:#94a3b8;text-decoration:none;border-radius:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;transition:background .12s,color .12s;font-size:11.5px}
        #sidebar a:hover,#sidebar a.active{background:#334155;color:#e2e8f0}
        .nav-chapter{margin-top:.55rem}
        .nav-chapter>.nav-title{display:block;padding:.25rem .45rem;color:#e2e8f0;font-weight:600;font-size:11.5px;text-decoration:none;border-radius:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .nav-chapter>.nav-title:hover{background:#334155}
        .nav-card{padding-left:1rem!important;font-size:11px!important;color:#7e93b5!important}
        .nav-subreports{display:block;padding:.2rem .45rem .2rem 1rem;font-size:11px;color:#475569;cursor:pointer;border-radius:4px;text-decoration:none;user-select:none}
        .nav-subreports:hover{background:#334155;color:#94a3b8}
        .nav-sub-chapters{display:none;border-left:1px solid #334155;margin:.1rem 0 .1rem .55rem}
        .nav-sub-chapters.open{display:block}
        .nav-sub-chapters a{padding-left:.6rem!important;font-size:11px!important;color:#64748b!important}
        .nav-sub-chapters a:hover,.nav-sub-chapters a.active{color:#e2e8f0!important}
        #search-box{width:100%;padding:.3rem .45rem;border-radius:4px;border:1px solid #475569;background:#0f172a;color:#e2e8f0;font-size:11.5px;margin-bottom:.6rem;outline:none}
        #search-box::placeholder{color:#64748b}

        /* ── Main content ─────────────────────────────────────────────── */
        #content{flex:1;min-width:0;overflow:auto}
        main{max-width:1280px;margin:0 auto;padding:.75rem 1.25rem 3rem}

        /* ── Hero / Chapter header ────────────────────────────────────── */
        /* Level-1 hero: main chapter header (blue gradient) */
        .hero{background:linear-gradient(135deg,#1e293b 0%,#1e3a5f 100%);color:#fff;padding:1.5rem 1.5rem .9rem;margin-top:.75rem;border-bottom:3px solid #3b82f6}
        .hero-title{font-size:1.35rem;font-weight:700;margin-bottom:.45rem;line-height:1.25}
        .hero-meta{display:flex;flex-wrap:wrap;gap:.35rem;margin-top:.3rem}
        .chip{display:inline-flex;align-items:center;padding:.2rem .55rem;border-radius:999px;background:rgba(148,163,184,.18);border:1px solid rgba(148,163,184,.3);color:#cbd5e1;font-size:.73rem}
        .chip-ok  {background:rgba(34,197,94,.18);border-color:rgba(34,197,94,.4);color:#bbf7d0}
        .chip-warn{background:rgba(251,191,36,.18);border-color:rgba(251,191,36,.4);color:#fef08a}
        .chip-crit{background:rgba(239,68,68,.18);border-color:rgba(239,68,68,.4);color:#fecaca}
        /* Level-2 hero: sub-command header (compact, no gradient) */
        .hero[data-nav-level="2"]{background:#eef2f7;color:#1e293b;padding:.6rem 1.5rem;margin-top:.5rem;border-bottom:1px solid #cbd5e1;border-left:4px solid #3b82f6}
        .hero[data-nav-level="2"] .hero-title{font-size:1rem;color:#1e3a5f;margin-bottom:.2rem}
        .hero[data-nav-level="2"] .chip{background:rgba(59,130,246,.1);border-color:rgba(59,130,246,.25);color:#334155}
        .hero[data-nav-level="2"] .chip-ok  {background:rgba(34,197,94,.12);color:#166534}
        .hero[data-nav-level="2"] .chip-warn{background:rgba(245,158,11,.12);color:#92400e}
        .hero[data-nav-level="2"] .chip-crit{background:rgba(239,68,68,.12);color:#991b1b}
        .chapter-body{margin-bottom:.5rem}

        /* ── Card / Section ───────────────────────────────────────────── */
        .card{background:#fff;border-radius:8px;margin:.65rem 0;box-shadow:0 1px 3px rgba(0,0,0,.07),0 1px 2px rgba(0,0,0,.05);overflow:hidden}
        .card-title{font-size:1rem;font-weight:700;color:#1e3a5f;padding:.75rem 1rem;cursor:pointer;user-select:none;display:flex;align-items:center;gap:.4rem;border-bottom:1px solid #e2e8f0}
        .card-title:hover{background:#f8fafc}
        .card-arrow{font-size:.7rem;transition:transform .18s;color:#64748b;flex-shrink:0}
        .card.collapsed .card-arrow{transform:rotate(-90deg)}
        .card.collapsed .card-body{display:none}
        .card-body{padding:.75rem 1rem 1rem}
        h3{font-size:.9rem;font-weight:600;color:#374151;margin:.65rem 0 .35rem}

        /* ── Key-Value grid ───────────────────────────────────────────── */
        .kv-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:.35rem .75rem;margin:.4rem 0 .65rem}
        .kv-row{display:flex;gap:.5rem;align-items:baseline;padding:.2rem .4rem;border-radius:4px;background:#fafafa;border:1px solid #f0f0f0}
        .kv-key{color:#64748b;font-size:12.5px;white-space:nowrap;flex-shrink:0;min-width:160px}
        .kv-val{font-weight:600;color:#111827;font-size:13px;overflow-wrap:anywhere}
        .score-badge{display:inline-block;padding:.15rem .55rem;border-radius:4px;font-weight:700;font-size:13px}
        .badge-ok  {background:#dcfce7;color:#15803d}
        .badge-warn{background:#fef9c3;color:#854d0e}
        .badge-crit{background:#fee2e2;color:#b91c1c}

        /* ── Tables ───────────────────────────────────────────────────── */
        .table-toolbar{display:flex;align-items:center;gap:.75rem;margin:.35rem 0 .25rem}
        .tbl-search{flex:1;max-width:340px;padding:.3rem .6rem;border:1px solid #d1d5db;border-radius:4px;font-size:12.5px;outline:none}
        .tbl-search:focus{border-color:#3b82f6;box-shadow:0 0 0 2px rgba(59,130,246,.15)}
        .row-count{font-size:11.5px;color:#9ca3af;white-space:nowrap}
        .table-wrap{width:100%;overflow-x:auto;-webkit-overflow-scrolling:touch}
        .tbl-large{max-height:520px;overflow-y:auto}
        table.data-table{width:100%;border-collapse:collapse;font-size:12.5px}
        table.data-table thead{position:sticky;top:0;z-index:2}
        th{background:#f1f5f9;padding:.4rem .65rem;text-align:left;font-weight:600;font-size:12px;color:#475569;border-bottom:2px solid #cbd5e1;white-space:nowrap;cursor:pointer;user-select:none;position:relative}
        th:hover{background:#e2e8f0}
        .sort-icon{margin-left:.25rem;opacity:.4;font-size:.7rem}
        th.sort-asc .sort-icon::after{content:"↑";opacity:1}
        th.sort-desc .sort-icon::after{content:"↓";opacity:1}
        th.sort-asc .sort-icon,th.sort-desc .sort-icon{opacity:1}
        td{padding:.32rem .65rem;border-bottom:1px solid #f1f5f9;vertical-align:top;white-space:nowrap}
        tr:hover td{background:#f8fafc}
        tr.vr{display:none}
        td.long-text{white-space:normal;overflow-wrap:anywhere;word-break:break-word;max-width:420px;line-height:1.35}
        p.caption{font-size:11.5px;color:#94a3b8;margin:.15rem 0 .35rem;font-style:italic}
        td.trend-up2{color:#dc2626;font-weight:700}
        td.trend-up {color:#f97316;font-weight:600}
        td.trend-dn {color:#16a34a}
        td.sev-crit {color:#dc2626;font-weight:700}
        td.sev-warn {color:#d97706;font-weight:600}
        td.sev-info {color:#2563eb}

        /* ── Alerts ───────────────────────────────────────────────────── */
        .alert{padding:.55rem .85rem;border-radius:6px;margin:.35rem 0;font-size:13px;border:1px solid transparent}
        .alert-crit{background:#fef2f2;border-color:#fecaca;border-left:4px solid #ef4444}
        .alert-warn{background:#fffbeb;border-color:#fde68a;border-left:4px solid #f59e0b}
        .alert-info{background:#eff6ff;border-color:#bfdbfe;border-left:4px solid #3b82f6}
        .alert-title{font-weight:600;margin-bottom:.2rem}
        .alert-icon{margin-right:.35rem}
        .alert-detail{color:#4b5563;font-size:12px;margin-top:.2rem;font-style:italic}
        .alert-advice{background:#f0fdf4;border:1px solid #bbf7d0;border-left:3px solid #16a34a;border-radius:4px;padding:.35rem .6rem;margin-top:.45rem;font-size:12.5px;color:#14532d;line-height:1.5}
        .advice-label{display:block;font-weight:700;font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:#15803d;margin-bottom:.15rem}

        /* ── Details accordion ────────────────────────────────────────── */
        details{border:1px solid #e2e8f0;border-radius:6px;margin:.4rem 0;overflow:hidden}
        details+details{margin-top:.3rem}
        summary.det-sum{padding:.5rem .8rem;cursor:pointer;font-weight:600;font-size:.855rem;color:#1e3a6e;list-style:none;background:#f8fafc;display:flex;align-items:center;gap:.4rem}
        summary.det-sum::-webkit-details-marker{display:none}
        summary.det-sum::before{content:"▶";font-size:.6rem;transition:transform .15s;color:#7e93b5;flex-shrink:0}
        details[open] summary.det-sum::before{transform:rotate(90deg)}
        details[open] summary.det-sum{border-bottom:1px solid #e2e8f0}
        .details-body{padding:.5rem .8rem}

        /* ── Misc ─────────────────────────────────────────────────────── */
        .body-text{color:#374151;margin:.3rem 0;font-size:13px}
        .spacer{height:.5rem}
        #back-top{position:fixed;bottom:1.5rem;right:1.5rem;background:#1e3a5f;color:#fff;border:none;border-radius:50%;width:40px;height:40px;font-size:1.1rem;cursor:pointer;opacity:0;transition:opacity .2s;z-index:100;display:flex;align-items:center;justify-content:center}
        #back-top.vis{opacity:.85}
        #back-top:hover{opacity:1}

        /* ── Print ────────────────────────────────────────────────────── */
        @media print{
          #sidebar,#back-top{display:none}
          body{background:#fff;font-size:11px}
          .card{box-shadow:none;border:1px solid #ccc;break-inside:avoid}
          .tbl-large{max-height:none;overflow-y:visible}
          tr.vr{display:table-row!important}
          .hero{background:#000!important;-webkit-print-color-adjust:exact;print-color-adjust:exact}
        }
        </style>
        </head>
        <body>
        <nav id="sidebar">
          <input id="search-box" placeholder="Search nav…" oninput="filterNav(this.value)">
          <h3>Contents</h3>
          <div id="nav-list"></div>
        </nav>
        <div id="content"><main id="report-root">
        """;

    const string Footer = """
        </main></div>
        <button id="back-top" title="Back to top" onclick="window.scrollTo({top:0,behavior:'smooth'})">↑</button>
        <script>
        /* ── Navigation builder ───────────────────────────────────────── */
        (function(){
          const nav = document.getElementById('nav-list');
          const root = document.getElementById('report-root');
          // Strip the repetitive "Dump Detective — " prefix that every sub-command adds
          function shortTitle(t){ return t.replace(/^Dump Detective\s*[—\-]\s*/i,''); }

          let curChDiv = null;   // current level-1 nav-chapter div
          let subChDiv = null;   // collapsible sub-chapters div (created on first level-2)
          let toggleEl = null;   // the ▸/▾ toggle anchor
          let subCount  = 0;     // running count of level-2 items for toggle label update

          root.querySelectorAll('.hero').forEach(function(h){
            const id    = h.id;
            const num   = id.slice(2);
            const raw   = h.querySelector('.hero-title')?.textContent?.trim() ?? '';
            const level = parseInt(h.dataset.navLevel ?? '1');

            if(level === 1){
              // Start a new chapter entry
              curChDiv = document.createElement('div');
              curChDiv.className = 'nav-chapter';
              subChDiv = null; toggleEl = null; subCount = 0;

              // Chapter title link
              const titleA = document.createElement('a');
              titleA.href = '#' + id;
              titleA.className = 'nav-title';
              titleA.textContent = shortTitle(raw);
              titleA.title = raw;
              curChDiv.appendChild(titleA);

              // Direct section cards within this chapter body
              const chBody = document.getElementById('chb' + num);
              if(chBody){
                chBody.querySelectorAll(':scope > .card').forEach(function(c){
                  const ca = document.createElement('a');
                  ca.href = '#' + c.id;
                  ca.className = 'nav-card';
                  const hdr = c.querySelector('.card-title');
                  ca.textContent = (hdr ? hdr.textContent : c.id).replace('▾','').trim();
                  ca.title = ca.textContent;
                  curChDiv.appendChild(ca);
                });
              }

              nav.appendChild(curChDiv);

            } else {
              // Level-2: sub-command header — goes into a collapsible group
              // inside the most recent level-1 chapter.
              if(!curChDiv) return;
              if(!subChDiv){
                // First time we see a level-2: create the toggle + collapsed list.
                // They are appended AFTER the card items so the visual order is:
                //   Chapter title
                //   └ card item 1, card item 2 …
                //   ▸ Sub-reports (N)   ← click to expand
                //     Heap Statistics
                //     Gen Summary …
                subChDiv = document.createElement('div');
                subChDiv.className = 'nav-sub-chapters';

                toggleEl = document.createElement('a');
                toggleEl.className = 'nav-subreports';
                toggleEl.textContent = '▸ Sub-reports';

                const sc = subChDiv, tg = toggleEl;
                toggleEl.onclick = function(e){
                  e.preventDefault();
                  const open = sc.classList.toggle('open');
                  tg.textContent = (open ? '▾ ' : '▸ ') + 'Sub-reports (' + subCount + ')';
                };

                curChDiv.appendChild(toggleEl);
                curChDiv.appendChild(subChDiv);
              }

              const a = document.createElement('a');
              a.href = '#' + id;
              a.textContent = shortTitle(raw);
              a.title = raw;
              subChDiv.appendChild(a);
              subCount++;
              toggleEl.textContent = '▸ Sub-reports (' + subCount + ')';
            }
          });
        })();

        /* ── Nav filter ───────────────────────────────────────────────── */
        function filterNav(q){
          q = q.toLowerCase();
          document.querySelectorAll('#nav-list a').forEach(function(a){
            a.style.display = (!q || a.textContent.toLowerCase().includes(q)) ? '' : 'none';
          });
        }

        /* ── Active nav highlight on scroll ──────────────────────────── */
        (function(){
          const targets = Array.from(document.querySelectorAll('.hero,.card'));
          const navLinks = document.querySelectorAll('#nav-list a');
          function update(){
            const mid = window.scrollY + window.innerHeight * 0.35;
            let best = null;
            for(const t of targets){
              if(t.offsetTop <= mid) best = t;
              else break;
            }
            if(best){
              const id = best.id;
              navLinks.forEach(function(a){
                a.classList.toggle('active', a.getAttribute('href') === '#' + id);
              });
            }
          }
          window.addEventListener('scroll', update, {passive:true});
        })();

        /* ── Back-to-top button ───────────────────────────────────────── */
        (function(){
          const btn = document.getElementById('back-top');
          window.addEventListener('scroll', function(){
            btn.classList.toggle('vis', window.scrollY > 400);
          }, {passive:true});
        })();




        /* ── Card collapse ────────────────────────────────────────────── */
        function toggleCard(id){
          const el = document.getElementById(id);
          if(el) el.classList.toggle('collapsed');
        }

        /* ── Table sort ───────────────────────────────────────────────── */
        var _sortState = {};
        function sortTable(tid, col){
          const tbl = document.getElementById('t' + tid);
          if(!tbl) return;
          const key = tid + '_' + col;
          const asc = _sortState[key] !== true;
          _sortState[key] = asc;
          // Update sort icons
          tbl.querySelectorAll('th').forEach(function(th,i){
            th.classList.remove('sort-asc','sort-desc');
            if(i === col) th.classList.add(asc ? 'sort-asc' : 'sort-desc');
          });
          const tbody = tbl.tBodies[0];
          const rows = Array.from(tbody.rows);
          rows.sort(function(a, b){
            const av = (a.cells[col]?.textContent ?? '').trim();
            const bv = (b.cells[col]?.textContent ?? '').trim();
            // Size strings first (KB/MB/GB/TB) — must run before parseFloat
            // because parseFloat("1.04 MB") === 1.04 which loses the unit
            const toBytes = function(s){
              const m = s.match(/^([\d,.]+)\s*(B|KB|MB|GB|TB)$/i);
              if(!m) return NaN;
              const mul = {B:1,KB:1024,MB:1048576,GB:1073741824,TB:1099511627776};
              return parseFloat(m[1].replace(/,/g,'')) * (mul[m[2].toUpperCase()]||1);
            };
            const ab = toBytes(av), bb = toBytes(bv);
            if(!isNaN(ab) && !isNaN(bb)) return asc ? ab-bb : bb-ab;
            // Plain numbers (strip commas/spaces before parse)
            const an = parseFloat(av.replace(/[, ]/g,''));
            const bn = parseFloat(bv.replace(/[, ]/g,''));
            if(!isNaN(an) && !isNaN(bn)) return asc ? an-bn : bn-an;
            return asc ? av.localeCompare(bv) : bv.localeCompare(av);
          });
          rows.forEach(function(r){ tbody.appendChild(r); });
        }

        /* ── Table filter / search ────────────────────────────────────── */
        function filterTable(tid){
          const q = (document.getElementById('ts' + tid)?.value ?? '').toLowerCase();
          const tbl = document.getElementById('t' + tid);
          if(!tbl) return;
          let vis = 0;
          Array.from(tbl.tBodies[0].rows).forEach(function(r){
            const match = !q || r.textContent.toLowerCase().includes(q);
            r.style.display = match ? '' : 'none';
            // When filtering, show all rows (override virtual-scroll hidden class)
            if(match){ r.classList.remove('vr'); vis++; }
          });
          const rc = document.getElementById('rc' + tid);
          if(rc) rc.textContent = (q ? vis + ' match' + (vis !== 1 ? 'es' : '') : tbl.tBodies[0].rows.length + ' rows');
        }
        </script>
        </body></html>
        """;
}
