#!/usr/bin/env python3
"""
json_to_html.py  –  Convert a DumpDetective JSON report to a self-contained HTML file.

Usage:
    python json_to_html.py report.json [report.html]

If the output path is omitted the HTML file is placed next to the JSON file
with the same stem and a .html extension.

The generated HTML is identical in structure and styling to what
DumpDetective produces directly with --output report.html.

Requirements: Python 3.8+ (stdlib only, no third-party packages needed).
"""

from __future__ import annotations

import html
import json
import re
import sys
from pathlib import Path


# ── HTML helpers ──────────────────────────────────────────────────────────────

def h(text: str) -> str:
    """HTML-escape a string (same as H() in HtmlSink.cs)."""
    return html.escape(str(text), quote=True)


def score_chip_class(chip_text: str) -> str:
    """Return the CSS class for a subtitle chip, same logic as HtmlSink."""
    cls = "chip"
    if chip_text.lower().startswith("score:") and "/100" in chip_text:
        num_str = chip_text.split(":", 1)[1].strip().split("/")[0].strip()
        try:
            sc = int(num_str)
            cls = "chip chip-ok" if sc >= 70 else "chip chip-warn" if sc >= 40 else "chip chip-crit"
        except ValueError:
            pass
    return cls


def render_subtitle_chips(subtitle: str | None) -> str:
    if not subtitle:
        return ""
    chips = [p.strip() for p in subtitle.split("|") if p.strip()]
    spans = "".join(
        f'<span class="{score_chip_class(p)}">{h(p)}</span>' for p in chips
    )
    return f'<div class="hero-meta">{spans}</div>'


def score_badge_html(key: str, value: str) -> str:
    """Wrap a score/health value in a colour badge, same as KeyValues in HtmlSink."""
    if "score" in key.lower() or "health" in key.lower():
        num_str = value.split("/")[0].strip()
        try:
            sc = int(num_str)
            cls = "badge-ok" if sc >= 70 else "badge-warn" if sc >= 40 else "badge-crit"
            return f'<span class="score-badge {cls}">{h(value)}</span>'
        except ValueError:
            pass
    return h(value)


def cell_class(cell: str) -> str:
    """Replicate per-cell CSS class logic from HtmlSink.Table()."""
    if cell in ("↑↑", "↑↑ ↑↑"):
        return ' class="trend-up2"'
    if cell in ("↑", "↑ ↑"):
        return ' class="trend-up"'
    if cell.startswith("↓"):
        return ' class="trend-dn"'
    if cell == "Critical":
        return ' class="sev-crit"'
    if cell == "Warning":
        return ' class="sev-warn"'
    if cell == "Info":
        return ' class="sev-info"'
    if len(cell) > 80:
        return ' class="long-text"'
    return ""


# ── Element renderers ─────────────────────────────────────────────────────────

_table_seq = 0


def render_elements(elements: list, buf: list) -> None:
    for elem in elements:
        etype = elem.get("type", "")
        if etype == "keyValues":
            render_key_values(elem, buf)
        elif etype == "table":
            render_table(elem, buf)
        elif etype == "alert":
            render_alert(elem, buf)
        elif etype == "text":
            buf.append(f'<p class="body-text">{h(elem.get("content", ""))}</p>\n')
        elif etype == "details":
            render_details(elem, buf)


def render_key_values(elem: dict, buf: list) -> None:
    title = elem.get("title")
    if title:
        buf.append(f"<h3>{h(title)}</h3>\n")
    buf.append('<div class="kv-grid">\n')
    for pair in elem.get("pairs", []):
        k = pair.get("key", "")
        v = pair.get("value", "")
        val_html = score_badge_html(k, v)
        buf.append(
            f'<div class="kv-row">'
            f'<span class="kv-key">{h(k)}</span>'
            f'<span class="kv-val">{val_html}</span>'
            f"</div>\n"
        )
    buf.append("</div>\n")


def render_table(elem: dict, buf: list) -> None:
    global _table_seq
    _table_seq += 1
    tid = _table_seq

    caption = elem.get("caption")
    headers = elem.get("headers", [])
    rows = elem.get("rows", [])
    large = len(rows) > 200

    if caption:
        buf.append(f'<p class="caption">{h(caption)}</p>\n')

    buf.append(
        f'<div class="table-toolbar">\n'
        f'  <input class="tbl-search" id="ts{tid}" placeholder="Filter rows…" '
        f'oninput="filterTable({tid})" autocomplete="off">\n'
        f'  <span class="row-count" id="rc{tid}">{len(rows):,} rows</span>\n'
        f"</div>\n"
    )

    wrap_cls = 'table-wrap tbl-large' if large else 'table-wrap'
    buf.append(f'<div class="{wrap_cls}" id="tw{tid}">\n')
    buf.append(f'<table id="t{tid}" class="data-table">\n')
    buf.append("<thead><tr>")
    for i, hdr in enumerate(headers):
        buf.append(
            f'<th onclick="sortTable({tid},{i})" title="Sort by {h(hdr)}">'
            f'{h(hdr)}<span class="sort-icon">⇅</span></th>'
        )
    buf.append("</tr></thead>\n<tbody>\n")

    for r_idx, row in enumerate(rows):
        row_class = ' class="vr"' if large and r_idx >= 200 else ""
        buf.append(f"<tr{row_class}>")
        for i, cell in enumerate(row):
            cc = cell_class(cell)
            buf.append(f"<td{cc}>{h(cell)}</td>")
        # Fill missing columns
        for _ in range(max(0, len(headers) - len(row))):
            buf.append("<td></td>")
        buf.append("</tr>\n")

    buf.append("</tbody></table></div>\n")

    if large:
        buf.append(
            f'<p class="caption">⚡ {len(rows):,} rows — '
            f"showing first 200; type in the filter box to search all rows.</p>\n"
        )


def render_alert(elem: dict, buf: list) -> None:
    level = elem.get("level", "info").lower()
    cls_map  = {"critical": "alert-crit", "warning": "alert-warn", "info": "alert-info"}
    icon_map = {"critical": "✗", "warning": "⚠", "info": "ℹ"}
    cls  = cls_map.get(level, "alert-info")
    icon = icon_map.get(level, "ℹ")
    title  = elem.get("title", "")
    detail = elem.get("detail")
    advice = elem.get("advice")

    buf.append(f'<div class="alert {cls}">')
    buf.append(f'<div class="alert-title"><span class="alert-icon">{icon}</span>{h(title)}</div>')
    if detail:
        buf.append(f'<div class="alert-detail">{h(detail)}</div>')
    if advice:
        buf.append(
            f'<div class="alert-advice">'
            f'<span class="advice-label">💡 Recommendation</span>'
            f"{h(advice)}"
            f"</div>"
        )
    buf.append("</div>\n")


def render_details(elem: dict, buf: list) -> None:
    open_attr = " open" if elem.get("open") else ""
    title = elem.get("title", "")
    buf.append(f"<details{open_attr}><summary class=\"det-sum\">{h(title)}</summary>"
               f'<div class="details-body">\n')
    render_elements(elem.get("elements", []), buf)
    buf.append("</div></details>\n")


# ── Main renderer ─────────────────────────────────────────────────────────────

_chapter_seq = 0
_section_seq = 0


def render_header(title: str, subtitle: str | None, level: int, buf: list) -> None:
    global _chapter_seq
    _chapter_seq += 1
    cid = _chapter_seq
    chips = render_subtitle_chips(subtitle)
    nav_level = str(level)

    # Strip the "Dump Detective — " prefix for level-2 (sub-command) headers
    display_title = title
    if level == 2:
        display_title = re.sub(r"^Dump Detective\s*[—\-]\s*", "", title, flags=re.IGNORECASE)

    buf.append(
        f'<div class="hero" id="ch{cid}" data-nav-level="{nav_level}">\n'
        f'  <h1 class="hero-title">{h(display_title)}</h1>\n'
        f"  {chips}\n"
        f"</div>\n"
        f'<div class="chapter-body" id="chb{cid}">\n'
    )


def render_section(title: str | None, elements: list, buf: list) -> None:
    global _section_seq
    if not elements:
        return

    if title:
        _section_seq += 1
        sid = _section_seq
        buf.append(
            f'<div class="card" id="s{sid}">\n'
            f'  <h2 class="card-title" onclick="toggleCard(\'s{sid}\')" '
            f'title="Click to collapse/expand">\n'
            f'    <span class="card-arrow">▾</span>{h(title)}\n'
            f"  </h2>\n"
            f'  <div class="card-body">\n'
        )
        render_elements(elements, buf)
        buf.append("  </div>\n</div>\n")
    else:
        # Root (implicit) section — no card wrapper
        render_elements(elements, buf)


def convert(data: dict) -> str:
    global _chapter_seq, _section_seq, _table_seq
    _chapter_seq = 0
    _section_seq = 0
    _table_seq   = 0

    title = data.get("title", "Dump Detective Report")
    chapters: list = data.get("chapters", [])

    buf: list[str] = []
    buf.append(DOC_HEADER.replace("Dump Detective Report", h(title), 1))

    for ch_idx, chapter in enumerate(chapters):
        ch_title    = chapter.get("title", "")
        ch_subtitle = chapter.get("subtitle")
        sections    = chapter.get("sections", [])

        # Close the previous chapter-body before a new hero
        if ch_idx > 0:
            buf.append("</div> <!-- /chapter-body -->\n")

        # navLevel is written by JsonSink (1 = top-level, 2 = sub-command inside analyze/trend)
        level = int(chapter.get("navLevel", 1))
        render_header(ch_title, ch_subtitle, level, buf)

        for section in sections:
            sec_title = section.get("title")
            elements  = section.get("elements", [])
            render_section(sec_title, elements, buf)

    # Close last chapter-body
    if chapters:
        buf.append("</div> <!-- /chapter-body -->\n")

    buf.append(FOOTER)
    return "".join(buf)


# ── Embedded CSS + JS (kept 1-to-1 with HtmlSink.cs) ─────────────────────────

DOC_HEADER = """\
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
.hero{background:linear-gradient(135deg,#1e293b 0%,#1e3a5f 100%);color:#fff;padding:1.5rem 1.5rem .9rem;margin-top:.75rem;border-bottom:3px solid #3b82f6}
.hero-title{font-size:1.35rem;font-weight:700;margin-bottom:.45rem;line-height:1.25}
.hero-meta{display:flex;flex-wrap:wrap;gap:.35rem;margin-top:.3rem}
.chip{display:inline-flex;align-items:center;padding:.2rem .55rem;border-radius:999px;background:rgba(148,163,184,.18);border:1px solid rgba(148,163,184,.3);color:#cbd5e1;font-size:.73rem}
.chip-ok  {background:rgba(34,197,94,.18);border-color:rgba(34,197,94,.4);color:#bbf7d0}
.chip-warn{background:rgba(251,191,36,.18);border-color:rgba(251,191,36,.4);color:#fef08a}
.chip-crit{background:rgba(239,68,68,.18);border-color:rgba(239,68,68,.4);color:#fecaca}
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
"""

FOOTER = """\
</main></div>
<button id="back-top" title="Back to top" onclick="window.scrollTo({top:0,behavior:'smooth'})">↑</button>
<script>
/* ── Navigation builder ───────────────────────────────────────── */
(function(){
  const nav = document.getElementById('nav-list');
  const root = document.getElementById('report-root');
  function shortTitle(t){ return t.replace(/^Dump Detective\\s*[—\\-]\\s*/i,''); }

  let curChDiv = null;
  let subChDiv = null;
  let toggleEl = null;
  let subCount  = 0;

  root.querySelectorAll('.hero').forEach(function(h){
    const id    = h.id;
    const num   = id.slice(2);
    const raw   = h.querySelector('.hero-title')?.textContent?.trim() ?? '';
    const level = parseInt(h.dataset.navLevel ?? '1');

    if(level === 1){
      curChDiv = document.createElement('div');
      curChDiv.className = 'nav-chapter';
      subChDiv = null; toggleEl = null; subCount = 0;

      const titleA = document.createElement('a');
      titleA.href = '#' + id;
      titleA.className = 'nav-title';
      titleA.textContent = shortTitle(raw);
      titleA.title = raw;
      curChDiv.appendChild(titleA);

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
      if(!curChDiv) return;
      if(!subChDiv){
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
  tbl.querySelectorAll('th').forEach(function(th,i){
    th.classList.remove('sort-asc','sort-desc');
    if(i === col) th.classList.add(asc ? 'sort-asc' : 'sort-desc');
  });
  const tbody = tbl.tBodies[0];
  const rows = Array.from(tbody.rows);
  rows.sort(function(a, b){
    const av = (a.cells[col]?.textContent ?? '').trim();
    const bv = (b.cells[col]?.textContent ?? '').trim();
    const toBytes = function(s){
      const m = s.match(/^([\\d,.]+)\\s*(B|KB|MB|GB|TB)$/i);
      if(!m) return NaN;
      const mul = {B:1,KB:1024,MB:1048576,GB:1073741824,TB:1099511627776};
      return parseFloat(m[1].replace(/,/g,'')) * (mul[m[2].toUpperCase()]||1);
    };
    const ab = toBytes(av), bb = toBytes(bv);
    if(!isNaN(ab) && !isNaN(bb)) return asc ? ab-bb : bb-ab;
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
    if(match){ r.classList.remove('vr'); vis++; }
  });
  const rc = document.getElementById('rc' + tid);
  if(rc) rc.textContent = (q ? vis + ' match' + (vis !== 1 ? 'es' : '') : tbl.tBodies[0].rows.length + ' rows');
}
</script>
</body></html>
"""


# ── Entry point ───────────────────────────────────────────────────────────────

def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    json_path = Path(sys.argv[1])
    if not json_path.exists():
        print(f"Error: file not found: {json_path}", file=sys.stderr)
        sys.exit(1)

    html_path = Path(sys.argv[2]) if len(sys.argv) >= 3 else json_path.with_suffix(".html")

    print(f"Reading  {json_path}")
    data = json.loads(json_path.read_text(encoding="utf-8"))

    print(f"Rendering HTML…")
    output = convert(data)

    html_path.write_text(output, encoding="utf-8")
    print(f"Written  {html_path}")


if __name__ == "__main__":
    main()
