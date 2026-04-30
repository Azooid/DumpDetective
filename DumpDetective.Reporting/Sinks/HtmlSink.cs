using System.Text;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Sinks;

/// <summary>
/// Modern redesign of the HTML sink — fully offline, no external dependencies.
/// Same features as <see cref="HtmlSink"/> but with a refreshed visual theme:
///   • Indigo-accent color palette with clean slate sidebar
///   • Pill-badge labels on Explain blocks
///   • Tighter card shadows, improved typography
///   • All layout and interaction features preserved
/// </summary>
public sealed class HtmlSink : IRenderSink
{
    readonly StreamWriter _w;
    bool _inSection;
    bool _chapterBodyOpen;
    int  _sectionSeq;
    int  _tableSeq;
    int  _chapterSeq;
    int  _treeSeq;
    int  _chartSeq;

    public bool    IsFile   => true;
    public string? FilePath => (_w.BaseStream as FileStream)?.Name;

    public HtmlSink(string path)
    {
        _w = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteDocHeader();
    }

    // ── IRenderSink ───────────────────────────────────────────────────────────

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
    {
        CloseSection();
        int id = ++_chapterSeq;
        int navLevelOverride = navLevel;
        string meta = string.Empty;
        if (subtitle is not null)
        {
            var parts = subtitle
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p =>
                {
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

        if (_chapterBodyOpen)
            _w.WriteLine("</div> <!-- /chapter-body -->");

        int resolvedLevel;
        if (navLevelOverride > 0)
            resolvedLevel = navLevelOverride;
        else if (CommandBase.SuppressVerbose)
            resolvedLevel = title.StartsWith("Per-Dump Report", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        else
            resolvedLevel = 1;

        string displayTitle = resolvedLevel >= 2
            ? System.Text.RegularExpressions.Regex.Replace(title, @"^Dump Detective\s*[—\-]\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : title;

        _w.WriteLine($"""
            <div class="hero" id="ch{id}" data-nav-level="{resolvedLevel}">
              <h1 class="hero-title">{H(displayTitle)}</h1>
              {meta}
            </div>
            <div class="chapter-body" id="chb{id}">
            """);
        _chapterBodyOpen = true;
    }

    public void Section(string title, string? sectionKey = null)
    {
        CloseSection();
        int id = ++_sectionSeq;
        string tip = sectionKey is not null ? TipHtmlByKey(sectionKey) ?? TipHtml(title) : TipHtml(title);
        _w.WriteLine($"""
            <div class="card" id="s{id}">
              <h2 class="card-title" onclick="toggleCard('s{id}')">
                <span class="card-arrow">▾</span>{H(title)}{tip}
              </h2>
              <div class="card-body">
            """);
        _inSection = true;
    }

    static string? TipHtmlByKey(string key)
    {
        foreach (var (k, text) in s_tooltipsByKey)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                string lines = string.Join("<br>", text.Split('\n'));
                return $""" <span class="tip-wrap" data-tip="{lines}" onmouseenter="showTip(this)" onmouseleave="hideTip()" onclick="event.stopPropagation()"><span class="tip-icon">?</span></span>""";
            }
        return null;
    }

    static string TipHtml(string title)
    {
        foreach (var (key, text) in s_tooltips)
            if (title.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                string lines = string.Join("<br>", text.Split('\n'));
                return $""" <span class="tip-wrap" data-tip="{lines}" onmouseenter="showTip(this)" onmouseleave="hideTip()" onclick="event.stopPropagation()"><span class="tip-icon">?</span></span>""";
            }
        return string.Empty;
    }

    static readonly (string Key, string Text)[] s_tooltips =
    [
        ("Dump Timeline",           "Temporal ordering of all analyzed dump snapshots.\nUse to correlate symptoms with capture time."),
        ("Incident Summary",        "Health scores and critical findings per snapshot.\nQuickly identifies the most degraded dump."),
        ("Overall Growth Summary",  "Memory, thread, and object count deltas across dumps.\nSteady growth without release indicates a leak."),
        ("Thread & Application",    "Thread pool queue depth and worker saturation.\nHigh pressure indicates starvation or lock contention."),
        ("Event Leak",              "Event handler subscriptions growing across snapshots.\nUnsubscribed handlers keep publisher objects alive."),
        ("Finalize Queue Detail",   "Finalizer queue size trend across snapshots.\nGrowth indicates IDisposable pattern violations."),
        ("Highly Referenced",       "Objects with the most incoming references.\nHigh counts often reveal retention roots."),
        ("Rooted Objects",          "Objects reachable from static or thread roots.\nRooted objects cannot be collected by GC."),
        ("Duplicate String",        "Identical string instances present in the heap.\nConsolidate with string.Intern or a shared dictionary."),
        ("String Duplicate",        "Identical string instances present in the heap.\nConsolidate with string.Intern or a shared dictionary."),
        ("Memory Leak Analysis",    "Types that accumulate across snapshots without release.\nIndicates missing Dispose calls or static retention."),
        ("Diagnosis Summary",       "Cross-snapshot comparison of key health signals.\nHighlights degradation trends and probable root causes."),
        ("Heap Statistics",         "Total managed heap size and per-segment breakdown.\nHigh committed memory may indicate GC pressure."),
        ("GC Generation Summary",   "Object counts by generation (Gen0/Gen1/Gen2/LOH).\nGen2 accumulation is the primary signal of a memory leak."),
        ("Heap Fragmentation",      "Free memory gaps between live objects on the heap.\nHigh fragmentation degrades allocation performance."),
        ("Large Objects",           "Objects over 85,000 bytes on the Large Object Heap.\nLOH is rarely compacted; leaks here cause OutOfMemoryException."),
        ("Exception Analysis",      "Exceptions found on thread stacks or in the heap.\nFrequent exceptions degrade throughput significantly."),
        ("Thread Analysis",         "Thread count, state, and call stacks.\nHigh counts or stuck threads indicate contention or deadlock."),
        ("Thread Pool",             "Managed thread pool workers, queue depth, and completions.\nQueue growth signals async starvation."),
        ("Async State Machine",     "In-flight async/await continuations.\nBacklog growth indicates a downstream bottleneck."),
        ("Deadlock",                "Cycles detected in lock-wait graphs.\nA single deadlock causes a complete application hang."),
        ("Finalizer Queue",         "Objects queued for finalization by the GC.\nGrowth indicates improper IDisposable usage."),
        ("GC Handle Table",         "GC-tracked and pinned object handles.\nExcess pinned handles fragment the managed heap."),
        ("Pinned Objects",          "Objects pinned in memory for native interop or unsafe code.\nExcessive pinning creates heap fragmentation."),
        ("Weak Reference",          "WeakReference objects tracked by the GC.\nExcessive weak refs may indicate problematic caching patterns."),
        ("Static Reference",        "Static fields that hold object references.\nStatic roots prevent GC from collecting referenced objects."),
        ("Timer Leak",              "Timer instances that were created but never disposed.\nLeaking timers keep callbacks and closures alive indefinitely."),
        ("Event Analysis",          "Event handler subscription counts by type.\nGrowth across dumps is a reliable leak indicator."),
        ("WCF Channel",             "Open WCF client channel instances.\nAborted or faulted channels must be explicitly closed."),
        ("DB Connection Pool",      "SQL/database connection pool usage and leaks.\nPool exhaustion causes timeouts and application hangs."),
        ("HTTP",                    "Outgoing HTTP requests and connection instances.\nStuck or leaked requests indicate downstream service issues."),
        ("Module List",             "Loaded .NET assemblies and native modules.\nUnexpected modules may indicate dynamic injection or leaks."),
    ];

    static readonly (string Key, string Text)[] s_tooltipsByKey =
    [
        ("findings",              "Scored health signals detected in this dump.\nCritical findings require immediate investigation."),
        ("memory",                "Managed heap distribution across GC generations.\nGen2 growth is the primary memory leak signal."),
        ("threads",               "Thread state, blocking, and thread pool pressure.\nBlocked threads and empty idle pool indicate contention or starvation."),
        ("async-backlog",         "Suspended async/await state machines.\nHigh counts indicate a downstream bottleneck or thread pool starvation."),
        ("exceptions",            "Exception objects on the heap and active exceptions on threads.\nActive exceptions indicate crash-related conditions."),
        ("leaks-handles",         "Resource handles, finalizer queue, timers, WCF channels, and connections.\nResource leaks can exhaust system limits before memory pressure is visible."),
        ("event-leaks",           "Event handler subscriptions preventing garbage collection.\nSubscriber objects cannot be collected while the publisher holds delegate references."),
        ("string-duplicates",     "Identical string values stored in separate objects.\nDeduplication with string.Intern or shared constants reduces heap pressure."),
        ("memory-leak-analysis",  "Gen2 percentage and type accumulation analysis.\nGen2 > 50% of heap is a strong managed memory leak signal."),
        ("heap-fragmentation",    "Free space between live heap objects.\nHigh fragmentation causes allocation failures and GC compaction overhead."),
        ("finalizer-queue",       "Objects queued for finalization before memory can be reclaimed.\nLarge queues indicate IDisposable pattern violations."),
        ("deadlock-analysis",     "Circular lock-wait cycles detected from Monitor ownership data.\nA single deadlock halts all threads sharing those locks."),
        ("gen-breakdown",         "Memory distribution across GC generations.\nGen2 > 70% of heap causes frequent expensive Gen2 collections."),
    ];

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null) _w.WriteLine($"<h3>{H(title)}</h3>");
        _w.WriteLine("<div class=\"kv-grid\">");
        foreach (var (k, v) in pairs)
        {
            string valHtml = H(v);
            if (k.Contains("score", StringComparison.OrdinalIgnoreCase) ||
              k.Contains("health", StringComparison.OrdinalIgnoreCase))
            {
              static string BadgeHtml(string part)
              {
                var numPart = part.Split('/')[0].Trim();
                if (int.TryParse(numPart, out int score))
                {
                  string badgeCls = score >= 70 ? "badge-ok" : score >= 40 ? "badge-warn" : "badge-crit";
                  return $"<span class=\"score-badge {badgeCls}\">{H(part)}</span>";
                }

                return H(part);
              }

              static string RenderScoreTrend(string text)
              {
                int arrowIdx = text.IndexOf(" → ", StringComparison.Ordinal);
                if (arrowIdx < 0)
                  return BadgeHtml(text.Trim());

                string left = text[..arrowIdx].Trim();
                string right = text[(arrowIdx + 3)..].Trim();
                string delta = string.Empty;
                var deltaMatch = System.Text.RegularExpressions.Regex.Match(right, @"^(.*?)(\s*\([^)]+\))\s*$");
                if (deltaMatch.Success)
                {
                  right = deltaMatch.Groups[1].Value.Trim();
                  delta = deltaMatch.Groups[2].Value.Trim();
                }

                string deltaHtml = delta.Length > 0 ? $" <span class=\"kv-delta\">{H(delta)}</span>" : string.Empty;
                return $"{BadgeHtml(left)} <span class=\"kv-arrow\">&rarr;</span> {BadgeHtml(right)}{deltaHtml}";
              }

              int diffSepIdx = v.IndexOf(" ⟹ ", StringComparison.Ordinal);
              if (diffSepIdx < 0)
                diffSepIdx = v.IndexOf("⟹", StringComparison.Ordinal);

              if (diffSepIdx >= 0)
              {
                string left = v[..diffSepIdx].Trim();
                string right = v[(v[diffSepIdx] == '⟹' ? diffSepIdx + 1 : diffSepIdx + 3)..].Trim();
                valHtml = $"{RenderScoreTrend(left)} <span class=\"kv-arrow\">⟹</span> {RenderScoreTrend(right)}";
              }
              else
              {
                valHtml = RenderScoreTrend(v);
              }
            }
            _w.WriteLine($"<div class=\"kv-row\"><span class=\"kv-key\">{H(k)}</span><span class=\"kv-val\">{valHtml}</span></div>");
        }
        _w.WriteLine("</div>");
    }

    public void Gauges(IReadOnlyList<(string Label, double Value, string Unit)> items, double barMax = 100.0)
    {
        _w.WriteLine("<div class=\"gauge-grid\">");
        foreach (var (label, value, unit) in items)
        {
            double pct       = barMax > 0 ? value / barMax * 100.0 : 0;
            double barWidth  = Math.Min(pct, 100.0);
            string levelCls  = pct >= 100 ? "g-crit" : pct >= 80 ? "g-high" : pct >= 50 ? "g-med" : "g-ok";

            _w.Write($"<div class=\"gauge-row {levelCls}\">");
            _w.Write($"<span class=\"gauge-label\">{H(label)}</span>");
            _w.Write($"<div class=\"gauge-track\"><div class=\"gauge-fill\" style=\"width:{barWidth:F1}%\"></div></div>");

            string valStr = value == Math.Floor(value)
                ? $"{value:N0}{H(unit)}"
                : $"{value:F1}{H(unit)}";
            if (pct > 100 && barMax > 0)
            {
                double mult = value / barMax;
                string multStr = mult >= 10 ? $"{mult:N1}" : $"{mult:F2}";
                _w.Write($"<span class=\"gauge-val\">{valStr}<span class=\"gauge-mult\">×{multStr}</span></span>");
            }
            else
            {
                _w.Write($"<span class=\"gauge-val\">{valStr}</span>");
            }
            _w.WriteLine("</div>");
        }
        _w.WriteLine("</div>");
    }

    // ── Chart types ───────────────────────────────────────────────────────────

    private static readonly string[] ChartPalette =
    [
        "#6366f1","#22c55e","#f59e0b","#ef4444","#06b6d4",
        "#8b5cf6","#ec4899","#84cc16","#f97316","#14b8a6",
        "#0ea5e9","#a855f7","#10b981","#f43f5e","#eab308",
    ];

    public void DonutChart(IReadOnlyList<(string Label, double Value)> segments,
                           string? caption = null, string? centerText = null)
    {
        double total = segments.Sum(s => s.Value);
        if (total <= 0) return;

        const double cx = 50, cy = 50, ro = 42, ri = 24;
        double angle = -Math.PI / 2;   // start at 12 o'clock
        var paths  = new System.Text.StringBuilder();
        var legend = new System.Text.StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            var (lbl, val) = segments[i];
            double frac = val / total;
            double sweep = frac >= 0.9999 ? Math.PI * 2 * 0.9999 : frac * Math.PI * 2;
            double endAngle = angle + sweep;
            int large = sweep > Math.PI ? 1 : 0;
            string color = ChartPalette[i % ChartPalette.Length];
            string pct = $"{frac * 100:F1}%";

            double x1 = cx + ro * Math.Cos(angle),  y1 = cy + ro * Math.Sin(angle);
            double x2 = cx + ro * Math.Cos(endAngle), y2 = cy + ro * Math.Sin(endAngle);
            double x3 = cx + ri * Math.Cos(endAngle), y3 = cy + ri * Math.Sin(endAngle);
            double x4 = cx + ri * Math.Cos(angle),  y4 = cy + ri * Math.Sin(angle);

            paths.Append($"""<path d="M{x1:F2},{y1:F2} A{ro},{ro} 0 {large},1 {x2:F2},{y2:F2} L{x3:F2},{y3:F2} A{ri},{ri} 0 {large},0 {x4:F2},{y4:F2} Z" fill="{color}" class="donut-seg"><title>{H(lbl)}: {pct}</title></path>""");
            legend.Append($"<li class=\"donut-li\"><span class=\"donut-dot\" style=\"background:{color}\"></span><span class=\"donut-lbl\">{H(lbl)}</span><span class=\"donut-pct\">{pct}</span></li>");
            angle = endAngle;
        }

        if (caption is not null) _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");
        _w.WriteLine("<div class=\"chart-card\"><div class=\"donut-layout\">");
        _w.WriteLine("<svg class=\"donut-svg\" viewBox=\"0 0 100 100\">");
        _w.Write(paths);
        if (centerText is not null)
        {
            var parts = centerText.Split('\n');
            _w.WriteLine($"<text x=\"50\" y=\"{(parts.Length > 1 ? "46" : "52")}\" class=\"donut-ctr-top\">{H(parts[0])}</text>");
            if (parts.Length > 1)
                _w.WriteLine($"<text x=\"50\" y=\"58\" class=\"donut-ctr-bot\">{H(parts[1])}</text>");
        }
        _w.WriteLine("</svg>");
        _w.WriteLine($"<ul class=\"donut-legend\">{legend}</ul>");
        _w.WriteLine("</div></div>");
    }

    public void StackedBar(IReadOnlyList<(string Label, double Value)> segments,
                           string? unit = null, string? caption = null,
                           string? valueMode = null)
    {
        double total = segments.Sum(s => s.Value);
        if (total <= 0) return;

        bool sizeMode = string.Equals(valueMode, "size", StringComparison.Ordinal);

        var track  = new System.Text.StringBuilder();
        var legend = new System.Text.StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            var (lbl, val) = segments[i];
            double pct = val / total * 100;
            string color = ChartPalette[i % ChartPalette.Length];
            string displayVal = sizeMode
                ? DumpDetective.Core.Utilities.DumpHelpers.FormatSize((long)val)
                : $"{val:F1}{H(unit ?? "")}";
            string tip = $"{H(lbl)}: {displayVal} ({pct:F1}%)";
            track.Append($"<div class=\"sbar-seg\" style=\"width:{pct:F2}%;background:{color}\" title=\"{tip}\"></div>");
            legend.Append($"<li class=\"sbar-li\"><span class=\"sbar-dot\" style=\"background:{color}\"></span><span class=\"sbar-lbl\">{H(lbl)}</span><span class=\"sbar-val\">{displayVal}</span><span class=\"sbar-pct\">({pct:F1}%)</span></li>");
        }

        if (caption is not null) _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");
        _w.WriteLine("<div class=\"chart-card\"><div class=\"sbar-wrap\">");
        _w.WriteLine($"<div class=\"sbar-track\">{track}</div>");
        _w.WriteLine($"<ul class=\"sbar-legend\">{legend}</ul>");
        _w.WriteLine("</div></div>");
    }

    public void Sparkline(IReadOnlyList<double> values, string? caption = null, string? unit = null,
                          string? valueMode = null)
    {
        if (values.Count == 0) return;

        bool sizeMode = string.Equals(valueMode, "size", StringComparison.Ordinal);
        string FormatVal(double v) => sizeMode
            ? DumpDetective.Core.Utilities.DumpHelpers.FormatSize((long)v)
            : $"{v:F1}{H(unit ?? "")}";

        double min = values.Min(), max = values.Max();
        double range = max - min;
        if (range == 0) range = 1;

        const int W = 220, H2 = 52;
        var pts = new System.Text.StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            double x = (double)i / Math.Max(values.Count - 1, 1) * W;
            double y = H2 - (values[i] - min) / range * (H2 - 8) - 4;
            pts.Append($"{x:F1},{y:F1} ");
        }

        // Filled area under the line
        double x0 = 0, xN = W;
        var area = new System.Text.StringBuilder();
        area.Append($"M{x0:F1},{H2} ");
        for (int i = 0; i < values.Count; i++)
        {
            double x = (double)i / Math.Max(values.Count - 1, 1) * W;
            double y = H2 - (values[i] - min) / range * (H2 - 8) - 4;
            area.Append($"L{x:F1},{y:F1} ");
        }
        area.Append($"L{xN:F1},{H2} Z");

        int id = ++_chartSeq;
        _w.WriteLine($"<div class=\"chart-card spark-wrap\">");
        if (caption is not null) _w.WriteLine($"<span class=\"spark-label\">{H(caption)}</span>");
        _w.WriteLine($"<svg class=\"spark-svg\" viewBox=\"0 0 {W} {H2}\">");
        _w.WriteLine($"<defs><linearGradient id=\"sg{id}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0%\" stop-color=\"#6366f1\" stop-opacity=\".18\"/><stop offset=\"100%\" stop-color=\"#6366f1\" stop-opacity=\"0\"/></linearGradient></defs>");
        _w.WriteLine($"<path d=\"{area}\" fill=\"url(#sg{id})\"/>");
        _w.WriteLine($"<polyline points=\"{pts}\" class=\"spark-line\"/>");
        _w.WriteLine("</svg>");
        _w.WriteLine($"<span class=\"spark-stats\">min <b>{FormatVal(min)}</b> &nbsp; avg <b>{FormatVal(values.Average())}</b> &nbsp; max <b>{FormatVal(max)}</b></span>");
        _w.WriteLine("</div>");
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        int tid = ++_tableSeq;
        bool large = rows.Count > 200;

        if (caption is not null) _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");

        _w.WriteLine($"""
            <div class="table-toolbar">
              <input class="tbl-search" id="ts{tid}" placeholder="Filter rows…" oninput="filterTable({tid})" autocomplete="off">
              <span class="row-count" id="rc{tid}">{rows.Count:N0} rows</span>
              <button class="tbl-csv-btn" onclick="exportCsv({tid})" title="Export visible rows as CSV">⬇ CSV</button>
            </div>
            """);

        _w.WriteLine($"<div class=\"table-wrap{(large ? " tbl-large" : "")}\" id=\"tw{tid}\">");
        _w.WriteLine($"<table id=\"t{tid}\" class=\"data-table\">");
        _w.WriteLine("<thead><tr>");
        for (int i = 0; i < headers.Length; i++)
            _w.Write($"<th onclick=\"sortTable({tid},{i})\" title=\"Sort by {H(headers[i])}\">{H(headers[i])}<span class=\"sort-icon\">⇅</span></th>");
        _w.WriteLine("</tr></thead>");
        _w.WriteLine("<tbody>");

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            _w.Write("<tr>");
            for (int i = 0; i < headers.Length; i++)
            {
                string cell = i < row.Length ? row[i] : string.Empty;
                string cellCls = "";
                string cellContent = H(cell);
                if (cell is "↑↑" or "↑↑ ↑↑") cellCls = " class=\"trend-up2\"";
                else if (cell is "↑" or "↑ ↑")  cellCls = " class=\"trend-up\"";
                else if (cell.StartsWith("↓"))    cellCls = " class=\"trend-dn\"";
                else if (cell.Length > 80)        cellCls = " class=\"long-text\"";
                else if (cell is "Critical")      { cellCls = " class=\"sev-crit\""; cellContent = "<span class=\"sev-badge sev-badge-crit\">✗ Critical</span>"; }
                else if (cell is "Warning")       { cellCls = " class=\"sev-warn\""; cellContent = "<span class=\"sev-badge sev-badge-warn\">⚠ Warning</span>"; }
                else if (cell is "Info")          { cellCls = " class=\"sev-info\""; cellContent = "<span class=\"sev-badge sev-badge-info\">ℹ Info</span>"; }
                _w.Write($"<td{cellCls}>{cellContent}</td>");
            }
            _w.WriteLine("</tr>");
        }

        _w.WriteLine("</tbody></table></div>");
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
    public void Reference(string label, string url)
        => _w.WriteLine($"<p class=\"ref-link\">📖 {H(label)} <a href=\"{H(url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{H(url)}</a></p>");

    public void BeginDetails(string title, bool open = false)
    {
        string openAttr = open ? " open" : string.Empty;
        _w.WriteLine($"<details{openAttr}><summary class=\"det-sum\">{H(title)}</summary><div class=\"details-body\">");
    }

    public void EndDetails() => _w.WriteLine("</div></details>");

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
    {
        // Rendered as a collapsed accordion in V2 — keeps reports clean;
        // click "About this section" to expand inline help.
        _w.WriteLine("<details class=\"explain-accordion\"><summary class=\"explain-summary\"><span class=\"explain-summary-icon\">ℹ</span>About this section</summary>");
        _w.WriteLine("<div class=\"explain-body\">");
        if (what is not null)
        {
            _w.WriteLine("<div class=\"explain-item\">");
            _w.WriteLine("<span class=\"explain-label\">What this means</span>");
            _w.WriteLine($"<p class=\"explain-text\">{H(what)}</p>");
            _w.WriteLine("</div>");
        }
        if (why is not null)
        {
            _w.WriteLine("<div class=\"explain-item\">");
            _w.WriteLine("<span class=\"explain-label\">Why it matters</span>");
            _w.WriteLine($"<p class=\"explain-text\">{H(why)}</p>");
            _w.WriteLine("</div>");
        }
        if (impact is not null)
        {
            _w.WriteLine("<div class=\"explain-item\">");
            _w.WriteLine("<span class=\"explain-label\">Potential impact</span>");
            _w.WriteLine($"<p class=\"explain-text\">{H(impact)}</p>");
            _w.WriteLine("</div>");
        }
        if (bullets is { Length: > 0 })
        {
            _w.WriteLine("<div class=\"explain-item\">");
            _w.WriteLine("<span class=\"explain-label\">What to look for</span>");
            _w.WriteLine("<ul class=\"explain-bullets\">");
            foreach (var b in bullets) _w.WriteLine($"<li>{H(b)}</li>");
            _w.WriteLine("</ul>");
            _w.WriteLine("</div>");
        }
        if (action is not null)
        {
            _w.WriteLine("<div class=\"explain-item explain-action\">");
            _w.WriteLine("<span class=\"explain-label\">Recommended action</span>");
            _w.WriteLine($"<p class=\"explain-text\">{H(action)}</p>");
            _w.WriteLine("</div>");
        }
        _w.WriteLine("</div></details>");
    }

    public void CallTree(IReadOnlyList<CallTreeNode> roots, string? caption = null, int topN = 20)
    {
        if (roots is null || roots.Count == 0) return;

        // Unique ID so multiple trees on the same page don't clash.
        var treeId = $"ctree{System.Threading.Interlocked.Increment(ref _treeSeq)}";

        if (caption is not null)
            _w.WriteLine($"<p class=\"caption\">{H(caption)}</p>");

        // Expand / Collapse All toolbar for this tree
        _w.WriteLine($"<div class=\"ctree-toolbar\">");
        _w.WriteLine($"  <button class=\"ct-expbtn\" onclick=\"ctExpandAll('{treeId}')\">⊞ Expand All</button>");
        _w.WriteLine($"  <button class=\"ct-expbtn\" onclick=\"ctCollapseAll('{treeId}')\">⊟ Collapse All</button>");
        _w.WriteLine("</div>");

        _w.WriteLine($"<div class=\"ctree-wrap\" id=\"{treeId}\">");
        _w.WriteLine("<table class=\"ctree-table\"><thead><tr>");
        _w.WriteLine("<th class=\"ctw-name\">Method / Module</th>");
        _w.WriteLine("<th class=\"ctw-bar\">Incl %</th>");
        _w.WriteLine("<th class=\"ctw-pct\">Incl %</th>");
        _w.WriteLine("<th class=\"ctw-pct\">Excl %</th>");
        _w.WriteLine("<th class=\"ctw-cnt\">Incl</th>");
        _w.WriteLine("<th class=\"ctw-cnt\">Excl</th>");
        _w.WriteLine("</tr></thead><tbody>");

        // topN limits the number of ROOT nodes shown; children are always fully rendered
        // (they start collapsed, so depth doesn't inflate visible rows).
        int rootsShown = 0;
        foreach (var node in roots)
        {
            if (rootsShown >= topN) break;
            rootsShown++;
            RenderTreeRow(node, 0);
        }
        _w.WriteLine("</tbody></table></div>");
    }

    private void RenderTreeRow(CallTreeNode node, int depth)
    {
        bool hasChildren = node.Children is { Count: > 0 };
        string toggleId  = hasChildren ? $"ct{System.Threading.Interlocked.Increment(ref _treeSeq)}" : string.Empty;
        string indent     = depth > 0 ? $"style=\"padding-left:{8 + depth * 18}px\"" : string.Empty;

        // Heat colour: 0–5% → neutral, 5–20% → orange, >20% → red (light/dark aware via CSS vars)
        string heatClass  = node.InclusivePct >= 20 ? "ct-hot2"
                          : node.InclusivePct >= 5  ? "ct-hot1"
                          : "ct-cool";

        // Bar width capped at 100 px (100% = full bar)
        int barPx = (int)Math.Min(node.InclusivePct, 100);

        _w.Write($"<tr class=\"ctrow {heatClass}\">");

        // Name cell — contains indent spacer, optional toggle chevron, method+module
        _w.Write("<td class=\"ctw-name\" " + indent + ">");
        if (hasChildren)
            _w.Write($"<span class=\"ct-toggle\" onclick=\"ctToggle('{toggleId}',this)\">▶</span> ");
        else
            _w.Write("<span class=\"ct-leaf\">·</span> ");
        _w.Write($"<span class=\"ct-method\" title=\"{H(node.Method)}\">{H(TruncateName(node.Method, 55))}</span>");
        if (node.Module.Length > 0)
            _w.Write($" <span class=\"ct-mod\">{H(TruncateName(node.Module, 30))}</span>");
        _w.WriteLine("</td>");

        // Bar cell
        _w.WriteLine($"<td class=\"ctw-bar\"><div class=\"ct-bar-bg\"><div class=\"ct-bar\" style=\"width:{barPx}%\"></div></div></td>");

        _w.WriteLine($"<td class=\"ctw-pct\">{node.InclusivePct:F1}%</td>");
        _w.WriteLine($"<td class=\"ctw-pct\">{node.ExclusivePct:F1}%</td>");
        _w.WriteLine($"<td class=\"ctw-cnt\">{node.InclusiveSamples:N0}</td>");
        _w.WriteLine($"<td class=\"ctw-cnt\">{node.ExclusiveSamples:N0}</td>");
        _w.WriteLine("</tr>");

        if (hasChildren)
        {
            _w.WriteLine($"<tr id=\"{toggleId}\" class=\"ct-children\" style=\"display:none\"><td colspan=\"6\" style=\"padding:0\">");
            _w.WriteLine("<table class=\"ctree-table ctree-nested\"><tbody>");
            foreach (var child in node.Children)
                RenderTreeRow(child, depth + 1);
            _w.WriteLine("</tbody></table></td></tr>");
        }
    }

    static string TruncateName(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

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
            _w.WriteLine("</div></div>");
            _inSection = false;
        }
    }

    static string H(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    void WriteDocHeader()
    {
        var now = DateTime.Now;
        _w.Write(DocHeader
            .Replace("{APP_VERSION}", DumpDetective.Core.Utilities.AppInfo.Version)
            .Replace("{GEN_DATE}", now.ToString("yyyy-MM-dd"))
            .Replace("{GEN_TIME}", now.ToString("HH:mm:ss"))
            .Replace("{GEN_DATETIME}", now.ToString("yyyy-MM-dd HH:mm:ss")));
    }

    // ── Document head + CSS ───────────────────────────────────────────────────

    // {APP_VERSION} is replaced at write time with AppInfo.Version (set from the entry assembly).
    const string DocHeader = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Dump Detective Report — {GEN_DATE}</title>
        <script>
        /* Apply theme before first paint to prevent white flash */
        (function(){
          var s=localStorage.getItem('dd-theme');
          if(s==='dark'||(!s&&window.matchMedia('(prefers-color-scheme:dark)').matches))
            document.documentElement.setAttribute('data-theme','dark');
        })();
        </script>
        <style>
        /* ── Reset / Base ───────────────────────────────────────────────────────── */
        *{box-sizing:border-box;margin:0;padding:0}
        html{scroll-behavior:smooth}
        body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Inter,Roboto,sans-serif;background:#f3f4f8;color:#1a1f2e;font-size:13px;line-height:1.55;display:flex;min-height:100vh}

        /* ── Sidebar nav (light, tree-style) ────────────────────────────────────── */
        #sidebar{position:sticky;top:0;height:100vh;width:220px;min-width:195px;overflow-y:auto;background:#fff;border-right:1px solid #e5e7eb;flex-shrink:0;display:flex;flex-direction:column;scrollbar-width:thin;scrollbar-color:#d1d5db transparent}
        /* Brand bar */
        .nav-brand{display:flex;align-items:center;gap:.6rem;padding:.75rem .85rem .7rem;border-bottom:1px solid #e9eaf0;flex-shrink:0;background:#fff}
        .nav-brand-icon{width:28px;height:28px;background:linear-gradient(135deg,#4f46e5,#7c3aed);border-radius:7px;display:flex;align-items:center;justify-content:center;font-size:11px;color:#fff;font-weight:900;flex-shrink:0;letter-spacing:-.5px;box-shadow:0 2px 6px rgba(79,70,229,.3)}
        .nav-brand-text{display:flex;flex-direction:column;overflow:hidden}
        .nav-brand-title{font-size:12px;font-weight:700;color:#1a1f2e;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .nav-brand-sub{font-size:9px;color:#9ca3af;letter-spacing:.06em;text-transform:uppercase}
        /* Search */
        .nav-search-wrap{padding:.5rem .75rem .45rem;border-bottom:1px solid #f3f4f6;flex-shrink:0;position:relative}
        #search-box{width:100%;padding:.3rem;border-radius:6px;border:1px solid #e5e7eb;background:#f9fafb .5rem center/12px no-repeat;color:#374151;font-size:11px;outline:none;transition:border-color .12s,background .12s}
        #search-box::placeholder{color:#9ca3af}
        #search-box:focus{border-color:#4f46e5;background:#fff}
        #search-clear{position:absolute;right:1rem;top:50%;transform:translateY(-50%);width:15px;height:15px;border-radius:50%;background:#d1d5db;color:#6b7280;border:none;cursor:pointer;font-size:9px;font-weight:700;display:none;align-items:center;justify-content:center;line-height:1;padding:0;transition:background .1s,color .1s}
        #search-clear:hover{background:#9ca3af;color:#fff}
        #search-clear.vis{display:flex}
        /* Nav content */
        .nav-inner{padding:.4rem .6rem .8rem;flex:1}
        .nav-section-label{font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;color:#9ca3af;padding:.6rem .4rem .2rem;margin-top:.1rem}
        #sidebar a{display:block;padding:.24rem .55rem;color:#6b7280;text-decoration:none;border-radius:5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;transition:background .1s,color .1s;font-size:11.5px;line-height:1.45}
        #sidebar a:hover{background:#f3f4f8;color:#1a1f2e}
        #sidebar a.active{background:#ede9fe;color:#4f46e5;font-weight:600}
        /* Chapter groups */
        .nav-chapter{margin-top:.3rem}
        .nav-chapter>.nav-title{display:flex;align-items:center;gap:.3rem;padding:.26rem .55rem;color:#374151;font-weight:700;font-size:11.5px;text-decoration:none;border-radius:5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .nav-chapter>.nav-title::before{content:"▸";font-size:.65rem;color:#4f46e5;flex-shrink:0}
        .nav-chapter>.nav-title:hover{background:#f3f4f8;color:#1a1f2e}
        .nav-card{padding-left:1.1rem!important;font-size:11px!important;color:#9ca3af!important}
        .nav-card:hover{color:#374151!important;background:#f3f4f8!important}
        /* Sub-reports toggle */
        .nav-subreports{display:block;padding:.2rem .55rem .2rem 1.05rem;font-size:11px;color:#9ca3af;cursor:pointer;border-radius:5px;text-decoration:none;user-select:none;transition:background .1s,color .1s}
        .nav-subreports:hover{background:#f3f4f8;color:#374151}
        .nav-sub-chapters{display:none;margin:.06rem 0 .06rem .55rem;border-left:2px solid #f3f4f6}
        .nav-sub-chapters.open{display:block}
        .nav-sub-chapters a{padding-left:.6rem!important;font-size:11px!important;color:#9ca3af!important}
        .nav-sub-chapters a:hover{color:#374151!important;background:#f3f4f8!important}
        .nav-sub-chapters a.active{color:#4f46e5!important;background:#ede9fe!important;font-weight:600}

        /* ── Main content ───────────────────────────────────────────────────────── */
        #content{flex:1;min-width:0;overflow:auto;background:#f3f4f8}
        main{max-width:1240px;margin:0 auto;padding:.8rem 1.25rem 3rem}

        /* ── Hero / Chapter header (top banner strip) ───────────────────────────── */
        .hero{background:#fff;border:1px solid #e5e7eb;border-radius:8px;padding:.85rem 1.25rem .75rem;margin-top:.75rem;border-left:4px solid #4f46e5;box-shadow:0 1px 3px rgba(0,0,0,.04)}
        .hero-title{font-size:1.15rem;font-weight:700;color:#1a1f2e;letter-spacing:-.01em;line-height:1.25;margin-bottom:.35rem}
        .hero-meta{display:flex;flex-wrap:wrap;gap:.3rem;margin-top:.25rem;align-items:center}
        .chip{display:inline-flex;align-items:center;padding:.15rem .55rem;border-radius:5px;background:#f3f4f8;border:1px solid #e5e7eb;color:#4b5563;font-size:11px;font-weight:500}
        .chip-ok  {background:#f0fdf4;border-color:#bbf7d0;color:#166534}
        .chip-warn{background:#fffbeb;border-color:#fde68a;color:#854d0e}
        .chip-crit{background:#fff1f2;border-color:#fecdd3;color:#9f1239}
        /* Sub-command hero (level 2) — compact, indented */
        .hero[data-nav-level="2"]{border-left-color:#7c3aed;background:#faf9ff;margin-top:.4rem;padding:.5rem 1.25rem}
        .hero[data-nav-level="2"] .hero-title{font-size:.95rem;color:#3730a3}
        .hero[data-nav-level="2"] .chip{background:#ede9fe;border-color:#ddd6fe;color:#4338ca}
        .hero[data-nav-level="2"] .chip-ok  {background:#f0fdf4;border-color:#bbf7d0;color:#166534}
        .hero[data-nav-level="2"] .chip-warn{background:#fffbeb;border-color:#fde68a;color:#854d0e}
        .hero[data-nav-level="2"] .chip-crit{background:#fff1f2;border-color:#fecdd3;color:#9f1239}
        .chapter-body{margin-bottom:.4rem}

        /* ── Card / Section ─────────────────────────────────────────────────────── */
        .card{background:#fff;border-radius:8px;margin:.5rem 0;border:1px solid #e5e7eb;box-shadow:0 1px 3px rgba(0,0,0,.04)}
        .card-title{font-size:.9rem;font-weight:700;color:#1a1f2e;padding:.6rem 1rem;cursor:pointer;user-select:none;display:flex;align-items:center;gap:.35rem;border-bottom:1px solid #f3f4f6;border-radius:8px 8px 0 0}
        .card.collapsed .card-title{border-radius:8px;border-bottom-color:transparent}
        .card-title:hover{background:#fafafa}
        .card-arrow{font-size:.6rem;transition:transform .15s;color:#9ca3af;flex-shrink:0}
        .card.collapsed .card-arrow{transform:rotate(-90deg)}
        .card.collapsed .card-body{display:none}
        .card-body{padding:.65rem 1rem .85rem}
        h3{font-size:.82rem;font-weight:600;color:#374151;margin:.55rem 0 .3rem}

        /* ── Key-Value grid ─────────────────────────────────────────────────────── */
        .kv-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:.28rem .7rem;margin:.35rem 0 .6rem}
        .kv-row{display:flex;gap:.45rem;align-items:baseline;padding:.18rem .45rem;border-radius:5px;background:#f9fafb;border:1px solid #f3f4f6}
        .kv-key{color:#6b7280;font-size:12px;white-space:nowrap;flex-shrink:0;min-width:155px}
        .kv-val{font-weight:600;color:#111827;font-size:12.5px;overflow-wrap:anywhere;display:flex;flex-wrap:wrap;align-items:center;gap:.25rem}
        .kv-arrow{color:#9ca3af;font-size:11.5px}
        .kv-delta{color:#6b7280;font-size:11.5px;font-weight:400}

        /* ── Metric gauges ───────────────────────────────────────────────────────── */
        .gauge-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(480px,1fr));gap:.3rem .7rem;margin:.35rem 0 .6rem}
        .gauge-row{display:flex;align-items:center;gap:.65rem;padding:.28rem .6rem;border-radius:6px;background:#f9fafb;border:1px solid #f3f4f6;overflow:hidden}
        .gauge-label{color:#6b7280;font-size:12px;white-space:nowrap;flex-shrink:0;min-width:155px}
        .gauge-track{flex:1;height:10px;background:#e9eaed;border-radius:999px;overflow:hidden;min-width:80px}
        .gauge-fill{height:100%;border-radius:999px;transition:width .2s}
        .g-ok   .gauge-fill{background:linear-gradient(90deg,#22c55e,#4ade80)}
        .g-med  .gauge-fill{background:linear-gradient(90deg,#f59e0b,#fbbf24)}
        .g-high .gauge-fill{background:linear-gradient(90deg,#f97316,#fb923c)}
        .g-crit .gauge-fill{background:linear-gradient(90deg,#ef4444,#f87171)}
        .gauge-val{font-weight:700;font-size:12.5px;color:#111827;white-space:nowrap;flex-shrink:0;text-align:right}
        .gauge-mult{font-size:10.5px;font-weight:600;color:#ef4444;margin-left:.3rem}

        /* ── Chart card (shared wrapper) ─────────────────────────────── */
        .chart-card{display:inline-flex;flex-direction:column;background:var(--bg-card,#fff);border:1px solid var(--border,#e5e7eb);border-radius:8px;padding:.6rem .9rem .5rem;margin:.4rem 0 .6rem;max-width:100%}
        [data-theme="dark"] .chart-card{background:#1e2235;border-color:#2a3050}

        /* ── Donut chart ─────────────────────────────────────────────────── */
        .donut-layout{display:flex;align-items:center;gap:1.5rem;flex-wrap:wrap}
        .donut-svg{width:200px;height:200px;flex-shrink:0;overflow:visible}
        .donut-seg{cursor:pointer;transition:opacity .15s}
        .donut-seg:hover{opacity:.75}
        .donut-ctr-top{text-anchor:middle;dominant-baseline:middle;font-size:9.5px;font-weight:700;fill:#111827}
        .donut-ctr-bot{text-anchor:middle;dominant-baseline:middle;font-size:7.5px;fill:#6b7280}
        .donut-legend{list-style:none;padding:0;margin:0;columns:2 160px;column-gap:1rem}
        .donut-li{display:flex;align-items:center;gap:.45rem;font-size:12px;break-inside:avoid;margin-bottom:.3rem;justify-content:space-between}
        .donut-dot{width:10px;height:10px;border-radius:50%;flex-shrink:0}
        .donut-lbl{color:#374151;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;flex:1 1 0;min-width:0}
        .donut-pct{color:#6b7280;font-weight:600;min-width:44px;text-align:right;flex-shrink:0;margin-left:.4rem}

        /* ── Stacked bar ─────────────────────────────────────────────── */
        .sbar-wrap{margin:.4rem 0 .6rem}
        .sbar-track{display:flex;height:22px;border-radius:6px;overflow:hidden;width:100%;max-width:640px;background:#f3f4f6}
        .sbar-seg{height:100%;transition:opacity .15s;cursor:pointer}
        .sbar-seg:hover{opacity:.8}
        .sbar-legend{list-style:none;padding:0;margin:.45rem 0 0;display:flex;flex-wrap:wrap;gap:.3rem 1.1rem}
        .sbar-li{display:flex;align-items:center;gap:.38rem;font-size:11.5px}
        .sbar-dot{width:9px;height:9px;border-radius:2px;flex-shrink:0}
        .sbar-lbl{color:#374151}
        .sbar-val{color:#111827;font-weight:600;margin-left:.15rem}
        .sbar-pct{color:#9ca3af;font-size:10.5px;margin-left:.1rem}

        /* ── Sparkline ────────────────────────────────────────────────── */
        .spark-wrap{display:flex;align-items:center;gap:.75rem;margin:.35rem 0 .5rem;flex-wrap:wrap;padding:.3rem .5rem;background:#f9fafb;border:1px solid #f3f4f6;border-radius:6px}
        .spark-label{font-size:12px;color:#374151;font-weight:600;white-space:nowrap}
        .spark-svg{flex-shrink:0;height:52px;width:220px;overflow:visible}
        .spark-line{fill:none;stroke:#6366f1;stroke-width:1.8;stroke-linejoin:round;stroke-linecap:round}
        .spark-stats{font-size:11.5px;color:#6b7280;white-space:nowrap}
        .spark-stats b{color:#111827;font-weight:600}
        .score-badge{display:inline-block;padding:.12rem .55rem;border-radius:5px;font-weight:700;font-size:12.5px}
        .badge-ok  {background:#dcfce7;color:#15803d}
        .badge-warn{background:#fef9c3;color:#854d0e}
        .badge-crit{background:#fee2e2;color:#b91c1c}

        /* ── Tables ─────────────────────────────────────────────────────────────── */
        /* Search bar ABOVE the table, outside the table-wrap */
        .table-toolbar{display:flex;align-items:center;gap:.65rem;margin:.3rem 0 .4rem}
        .tbl-search{padding:.27rem .65rem .27rem 1.9rem;border:1px solid #e5e7eb;border-radius:6px;font-size:11.5px;outline:none;background:#f9fafb url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='%239ca3af' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'%3E%3Ccircle cx='11' cy='11' r='8'/%3E%3Cpath d='m21 21-4.35-4.35'/%3E%3C/svg%3E") .5rem center/12px no-repeat;transition:border-color .12s,background .12s;min-width:200px;max-width:320px}
        .tbl-search:focus{border-color:#4f46e5;background:#fff;box-shadow:0 0 0 2px rgba(79,70,229,.09)}
        .row-count{font-size:11px;color:#9ca3af;white-space:nowrap;margin-left:.1rem}
        .table-wrap{width:100%;overflow-x:auto;-webkit-overflow-scrolling:touch;border:1px solid #e5e7eb;border-radius:7px;margin-top:.1rem}
        .tbl-large{max-height:480px;overflow-y:auto}
        table.data-table{width:100%;border-collapse:collapse;font-size:12px}
        table.data-table thead{position:sticky;top:0;z-index:2}
        th{background:#f8f9fc;padding:.38rem .7rem;text-align:left;font-weight:600;font-size:11px;color:#374151;border-bottom:1px solid #e5e7eb;white-space:nowrap;cursor:pointer;user-select:none}
        th:hover{background:#ede9fe;color:#4f46e5}
        .sort-icon{margin-left:.22rem;opacity:.3;font-size:.6rem}
        th.sort-asc .sort-icon::after{content:"↑"}
        th.sort-desc .sort-icon::after{content:"↓"}
        th.sort-asc .sort-icon,th.sort-desc .sort-icon{opacity:1;color:#4f46e5}
        td{padding:.32rem .7rem;border-bottom:1px solid #f3f4f6;vertical-align:top;white-space:nowrap;font-size:12px;color:#1a1f2e}
        tr:last-child td{border-bottom:none}
        tr:nth-child(even) td{background:#fafbff}
        tr:hover td{background:#f5f3ff}
        td.long-text{white-space:normal;overflow-wrap:anywhere;word-break:break-word;max-width:400px;line-height:1.4}
        p.caption{font-size:11px;color:#9ca3af;margin:.12rem 0 .3rem;font-style:italic}
        td.trend-up2{color:#dc2626;font-weight:700}
        td.trend-up {color:#ea580c;font-weight:600}
        td.trend-dn {color:#16a34a}
        td.sev-crit {}
        td.sev-warn {}
        td.sev-info {}
        .sev-badge{display:inline-flex;align-items:center;gap:.28rem;padding:.14rem .55rem;border-radius:999px;font-size:11px;font-weight:700;white-space:nowrap}
        .sev-badge-crit{background:#fff1f2;border:1px solid #fecdd3;color:#be123c}
        .sev-badge-warn{background:#fffbeb;border:1px solid #fde68a;color:#92400e}
        .sev-badge-info{background:#f5f3ff;border:1px solid #ddd6fe;color:#3730a3}

        /* ── Alerts ─────────────────────────────────────────────────────────────── */
        .alert{padding:.5rem .85rem;border-radius:7px;margin:.3rem 0;font-size:12.5px;border:1px solid transparent}
        .alert-crit{background:#fff1f2;border-color:#fecdd3;border-left:4px solid #ef4444}
        .alert-warn{background:#fffbeb;border-color:#fde68a;border-left:4px solid #f59e0b}
        .alert-info{background:#f5f3ff;border-color:#ddd6fe;border-left:4px solid #4f46e5}
        .alert-title{font-weight:700;margin-bottom:.2rem;font-size:12.5px;display:flex;align-items:center;gap:.3rem}
        .alert-icon{font-size:.95rem}
        .alert-detail{color:#4b5563;font-size:11.5px;margin-top:.18rem;font-style:italic}
        .alert-advice{background:#f0fdf4;border:1px solid #bbf7d0;border-left:3px solid #22c55e;border-radius:6px;padding:.5rem .8rem;margin-top:.45rem;font-size:12.5px;color:#14532d;line-height:1.55}
        .advice-label{display:flex;align-items:center;gap:.35rem;font-weight:700;font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:#16a34a;margin-bottom:.2rem}

        /* ── Details accordion ──────────────────────────────────────────────────── */
        details{border:1px solid #e5e7eb;border-radius:7px;margin:.35rem 0;overflow:hidden}
        details+details{margin-top:.25rem}
        summary.det-sum{padding:.42rem .8rem;cursor:pointer;font-weight:600;font-size:.825rem;color:#1a1f2e;list-style:none;background:#f8f9fc;display:flex;align-items:center;gap:.35rem}
        summary.det-sum::-webkit-details-marker{display:none}
        summary.det-sum::before{content:"▶";font-size:.55rem;transition:transform .13s;color:#4f46e5;flex-shrink:0}
        details[open] summary.det-sum::before{transform:rotate(90deg)}
        details[open] summary.det-sum{border-bottom:1px solid #e5e7eb}
        .details-body{padding:.45rem .8rem}

        /* ── Explain block ──────────────────────────────────────────────────────── */
        /* ── Explain accordion ("About this section") ──────────────────────────── */
        .explain-accordion{border:1px solid #e5e7eb;border-radius:7px;margin:.4rem 0 .65rem;overflow:hidden;background:#fafafa}
        .explain-accordion[open]{background:#fff}
        .explain-summary{padding:.32rem .75rem;cursor:pointer;list-style:none;display:flex;align-items:center;gap:.45rem;font-size:11.5px;font-weight:600;color:#6b7280;user-select:none;background:#f9fafb;border-bottom:1px solid transparent;transition:color .12s,background .12s}
        .explain-accordion[open] .explain-summary{color:#4f46e5;background:#f5f3ff;border-bottom-color:#e5e7eb}
        .explain-summary::-webkit-details-marker{display:none}
        .explain-summary-icon{font-size:12px;line-height:1;flex-shrink:0;color:#a5b4fc}
        .explain-accordion[open] .explain-summary-icon{color:#4f46e5}
        .explain-body{display:flex;flex-direction:column}
        .explain-item{padding:.4rem .85rem;border-bottom:1px solid #f3f4f6;display:flex;flex-direction:column;gap:.12rem}
        .explain-item:last-child{border-bottom:none}
        .explain-label{display:inline-flex;align-items:center;background:#ede9fe;color:#4f46e5;font-size:9px;font-weight:700;text-transform:uppercase;letter-spacing:.1em;border-radius:999px;padding:.14em .65em;margin-bottom:.08rem;width:fit-content}
        .explain-text{font-size:12.5px;color:#374151;line-height:1.6;margin:0}
        .explain-bullets{margin:.12rem 0 0 1.1rem;padding:0;list-style:disc;color:#374151;font-size:12.5px;line-height:1.6}
        .explain-action{background:#f0fdf4}
        .explain-action .explain-label{background:#dcfce7;color:#15803d}

        /* ── Misc ────────────────────────────────────────────────────────────────── */
        .body-text{color:#374151;margin:.25rem 0;font-size:13px;line-height:1.6}
        .ref-link{color:#6b7280;font-size:11.5px;margin:.35rem 0 .15rem;font-style:italic}
        .ref-link a{color:#4f46e5;text-decoration:none}
        .ref-link a:hover{text-decoration:underline}
        .spacer{height:.4rem}
        #back-top{position:fixed;bottom:1.5rem;right:1.5rem;background:#4f46e5;color:#fff;border:none;border-radius:50%;width:36px;height:36px;font-size:1rem;cursor:pointer;opacity:0;transition:opacity .2s;z-index:100;display:flex;align-items:center;justify-content:center;box-shadow:0 2px 8px rgba(79,70,229,.35)}
        #back-top.vis{opacity:.85}
        #back-top:hover{opacity:1;background:#3730a3}

        /* ── Scrollbars (global thin style) ───────────────────────────────────────────── */
        /* Firefox */
        *{scrollbar-width:thin;scrollbar-color:#d1d5db transparent}
        /* Chrome / Edge / Safari */
        ::-webkit-scrollbar{width:5px;height:5px}
        ::-webkit-scrollbar-track{background:transparent}
        ::-webkit-scrollbar-thumb{background:#d1d5db;border-radius:99px}
        ::-webkit-scrollbar-thumb:hover{background:#9ca3af}
        ::-webkit-scrollbar-corner{background:transparent}

        /* ── Severity summary bar (sidebar) ─────────────────────────────────────── */
        #sev-bar{display:flex;gap:.25rem;padding:.3rem .6rem;border-bottom:1px solid #f3f4f6;flex-shrink:0;flex-wrap:nowrap;align-items:center}
        .sev-pill{display:inline-flex;align-items:center;gap:.2rem;padding:.1rem .35rem;border-radius:999px;font-size:10px;font-weight:700;cursor:pointer;border:1px solid transparent;transition:opacity .12s,background .1s;user-select:none;white-space:nowrap;line-height:1.4}
        .sev-pill-crit{background:#fff1f2;border-color:#fecdd3;color:#9f1239}
        .sev-pill-warn{background:#fffbeb;border-color:#fde68a;color:#854d0e}
        .sev-pill-info{background:#f5f3ff;border-color:#ddd6fe;color:#4338ca}
        .sev-pill:hover{opacity:.75}
        /* ── Expand / Collapse toolbar ───────────────────────────────────────────── */
        #expand-bar{display:flex;align-items:center;gap:.45rem;padding:.38rem 1.25rem;background:#fff;border-bottom:1px solid #e5e7eb;position:sticky;top:0;z-index:10;box-shadow:0 1px 4px rgba(0,0,0,.05)}
        .exp-btn{padding:.2rem .65rem;border:1px solid #e5e7eb;border-radius:5px;background:#f9fafb;color:#374151;font-size:11px;font-weight:600;cursor:pointer;transition:background .1s,border-color .1s;line-height:1.4}
        .exp-btn:hover{background:#ede9fe;border-color:#a5b4fc;color:#4f46e5}
        #exp-bar-sep{width:1px;height:14px;background:#e5e7eb;margin:0 .1rem;flex-shrink:0}
        #exp-bar-label{font-size:10.5px;color:#9ca3af}
        /* ── CSV export button ───────────────────────────────────────────────────── */
        .tbl-csv-btn{padding:.2rem .6rem;border:1px solid #e5e7eb;border-radius:5px;background:#f9fafb;color:#374151;font-size:11px;font-weight:600;cursor:pointer;transition:background .1s,border-color .1s;margin-left:auto;line-height:1.4}
        .tbl-csv-btn:hover{background:#ede9fe;border-color:#a5b4fc;color:#4f46e5}
        /* ── Jump-to-critical floating button ────────────────────────────────────── */
        #jump-crit{position:fixed;bottom:4.2rem;right:1.5rem;background:#ef4444;color:#fff;border:none;border-radius:50%;width:36px;height:36px;font-size:.85rem;cursor:pointer;opacity:0;transition:opacity .2s;z-index:100;display:flex;align-items:center;justify-content:center;box-shadow:0 2px 8px rgba(239,68,68,.4);pointer-events:none}
        #jump-crit.vis{opacity:.85;pointer-events:auto}
        #jump-crit:hover{opacity:1;background:#dc2626}

        /* ── Metric Tooltips ─────────────────────────────────────────────────────── */
        .tip-wrap{display:inline-flex;align-items:center;margin-left:.35rem;flex-shrink:0;cursor:default}
        .tip-icon{display:inline-flex;align-items:center;justify-content:center;width:14px;height:14px;border-radius:50%;background:#ddd6fe;color:#4f46e5;font-size:8px;font-weight:700;line-height:1;user-select:none;flex-shrink:0;transition:background .1s,color .1s}
        .tip-wrap:hover .tip-icon{background:#4f46e5;color:#fff}
        #ftip{display:none;position:fixed;background:#1e1b4b;color:#e0e7ff;font-size:11.5px;font-weight:400;line-height:1.5;padding:.5rem .75rem;border-radius:7px;white-space:normal;width:280px;box-shadow:0 4px 16px rgba(0,0,0,.25);pointer-events:none;z-index:9999;text-align:left}

        /* ── Call Tree ──────────────────────────────────────────────────────────── */
        .ctree-wrap{margin:.35rem 0;border:1px solid #e5e7eb;border-radius:7px;overflow:hidden}
        .ctree-toolbar{display:flex;gap:.4rem;margin:.2rem 0 .35rem}
        .ct-expbtn{padding:.18rem .55rem;border:1px solid #e5e7eb;border-radius:5px;background:#f9fafb;color:#374151;font-size:11px;font-weight:600;cursor:pointer;transition:background .1s,border-color .1s;line-height:1.4}
        .ct-expbtn:hover{background:#ede9fe;border-color:#a5b4fc;color:#4f46e5}
        .ctree-table{width:100%;border-collapse:collapse;font-size:11.5px}
        .ctree-table thead th{background:#f8f9fc;padding:.3rem .65rem;font-size:11px;font-weight:600;color:#374151;border-bottom:1px solid #e5e7eb;white-space:nowrap;text-align:left}
        .ctw-name{width:55%;min-width:200px}
        .ctw-bar{width:120px;min-width:80px}
        .ctw-pct{width:60px;text-align:right;font-variant-numeric:tabular-nums}
        .ctw-cnt{width:65px;text-align:right;font-variant-numeric:tabular-nums;color:#6b7280;font-size:11px}
        .ctrow td{padding:.25rem .65rem;border-bottom:1px solid #f3f4f6;vertical-align:middle}
        .ctrow:last-child td{border-bottom:none}
        .ctrow:hover td{background:#f5f3ff}
        .ct-hot2{background:#fff5f5}
        .ct-hot1{background:#fffbf0}
        .ct-cool{background:#fff}
        .ct-toggle{cursor:pointer;font-size:.55rem;color:#4f46e5;user-select:none;transition:transform .1s;display:inline-block}
        .ct-toggle.open{transform:rotate(90deg)}
        .ct-leaf{color:#d1d5db;font-size:.7rem}
        .ct-method{font-weight:500;color:#1a1f2e;font-size:11.5px}
        .ct-mod{font-size:10.5px;color:#9ca3af;padding:.1em .4em;background:#f3f4f8;border-radius:4px;margin-left:.25rem}
        .ct-bar-bg{height:8px;background:#f3f4f6;border-radius:999px;overflow:hidden;min-width:60px}
        .ct-bar{height:8px;background:linear-gradient(90deg,#6366f1,#8b5cf6);border-radius:999px;transition:width .2s}
        .ct-hot2 .ct-bar{background:linear-gradient(90deg,#ef4444,#f97316)}
        .ct-hot1 .ct-bar{background:linear-gradient(90deg,#f59e0b,#fbbf24)}
        .ctree-nested{width:100%}

        /* ── Print ───────────────────────────────────────────────────────────────── */
        @media print{
          #sidebar,#back-top{display:none}
          body{background:#fff;font-size:11px}
          .card{box-shadow:none;border:1px solid #ccc;break-inside:avoid}
          .tbl-large{max-height:none;overflow-y:visible}
          .hero{background:#fff!important;border-bottom:1px solid #e5e7eb!important;position:static!important}
        }

        /* ── Dark mode ──────────────────────────────────────────────────────────── */
        [data-theme="dark"] body{background:#0d0f17;color:#d1d5e0}
        [data-theme="dark"] #sidebar{background:#13151f;border-right-color:#252840}
        [data-theme="dark"] #sidebar::-webkit-scrollbar-thumb{background:#303450}
        [data-theme="dark"] .nav-brand{background:#13151f;border-bottom-color:#252840}
        [data-theme="dark"] .nav-brand-title{color:#e2e8f0}
        [data-theme="dark"] .nav-brand-sub{color:#5b6280}
        [data-theme="dark"] .nav-search-wrap{border-bottom-color:#1e2035}
        [data-theme="dark"] #search-box{background:#1e2035;border-color:#2e3154;color:#d1d5e0}
        [data-theme="dark"] #search-box:focus{border-color:#6366f1;background:#252840}
        [data-theme="dark"] #search-clear{background:#2e3154;color:#8892b0}
        [data-theme="dark"] #search-clear:hover{background:#4a5070;color:#d1d5e0}
        [data-theme="dark"] #sidebar a{color:#8892b0}
        [data-theme="dark"] #sidebar a:hover{background:#1e2035;color:#e2e8f0}
        [data-theme="dark"] #sidebar a.active{background:#2d2b5e;color:#818cf8}
        [data-theme="dark"] .nav-chapter>.nav-title{color:#c5cae0}
        [data-theme="dark"] .nav-chapter>.nav-title:hover{background:#1e2035;color:#e2e8f0}
        [data-theme="dark"] .nav-section-label{color:#4a5070}
        [data-theme="dark"] .nav-card{color:#5b6280!important}
        [data-theme="dark"] .nav-card:hover{color:#c5cae0!important;background:#1e2035!important}
        [data-theme="dark"] .nav-subreports{color:#5b6280}
        [data-theme="dark"] .nav-subreports:hover{background:#1e2035;color:#8892b0}
        [data-theme="dark"] .nav-sub-chapters{border-left-color:#252840}
        [data-theme="dark"] .nav-sub-chapters a{color:#5b6280!important}
        [data-theme="dark"] .nav-sub-chapters a:hover{color:#c5cae0!important;background:#1e2035!important}
        [data-theme="dark"] .nav-sub-chapters a.active{color:#818cf8!important;background:#2d2b5e!important}
        [data-theme="dark"] #content{background:#0d0f17}
        [data-theme="dark"] .hero{background:#13151f;border-color:#252840;box-shadow:none}
        [data-theme="dark"] .hero-title{color:#e2e8f0}
        [data-theme="dark"] .chip{background:#1e2035;border-color:#2e3154;color:#8892b0}
        [data-theme="dark"] .hero[data-nav-level="2"]{background:#181a28}
        [data-theme="dark"] .hero[data-nav-level="2"] .hero-title{color:#a5b4fc}
        [data-theme="dark"] .card{background:#13151f;border-color:#252840;box-shadow:none}
        [data-theme="dark"] .card-title{color:#d1d5e0;border-bottom-color:#252840}
        [data-theme="dark"] .card-title:hover{background:#1a1d2b}
        [data-theme="dark"] .card-arrow{color:#5b6280}
        [data-theme="dark"] h3{color:#8892b0}
        [data-theme="dark"] .kv-row{background:#1a1d2b;border-color:#252840}
        [data-theme="dark"] .kv-key{color:#5b6280}
        [data-theme="dark"] .kv-val{color:#d1d5e0}
        [data-theme="dark"] .gauge-row{background:#1a1d2b;border-color:#252840}
        [data-theme="dark"] .gauge-label{color:#5b6280}
        [data-theme="dark"] .gauge-track{background:#252840}
        [data-theme="dark"] .gauge-val{color:#d1d5e0}
        [data-theme="dark"] th{background:#1a1d2b;color:#8892b0;border-bottom-color:#252840}
        [data-theme="dark"] th:hover{background:#2d2b5e;color:#818cf8}
        [data-theme="dark"] td{color:#c5cae0;border-bottom-color:#1e2035}
        [data-theme="dark"] tr:nth-child(even) td{background:#161826}
        [data-theme="dark"] tr:hover td{background:#1e2242}
        [data-theme="dark"] .table-wrap{border-color:#252840}
        [data-theme="dark"] .tbl-search{background:#1a1d2b;border-color:#2e3154;color:#d1d5e0}
        [data-theme="dark"] .tbl-search:focus{border-color:#6366f1;background:#252840;box-shadow:0 0 0 2px rgba(99,102,241,.15)}
        [data-theme="dark"] .row-count{color:#4a5070}
        [data-theme="dark"] .alert-crit{background:#2a1218;border-color:#7f1d1d}
        [data-theme="dark"] .alert-warn{background:#1f1a0c;border-color:#78350f}
        [data-theme="dark"] .alert-info{background:#17152e;border-color:#312e7a}
        [data-theme="dark"] .alert-detail{color:#8892b0}
        [data-theme="dark"] .alert-advice{background:#0d2818;border-color:#166534;border-left-color:#4ade80;color:#bbf7d0}
        [data-theme="dark"] .advice-label{color:#4ade80}
        [data-theme="dark"] details{border-color:#252840}
        [data-theme="dark"] summary.det-sum{background:#1a1d2b;color:#d1d5e0}
        [data-theme="dark"] details[open] summary.det-sum{border-bottom-color:#252840}
        [data-theme="dark"] .explain-accordion{background:#13151f;border-color:#252840}
        [data-theme="dark"] .explain-accordion[open]{background:#161826}
        [data-theme="dark"] .explain-summary{background:#1a1d2b;color:#5b6280}
        [data-theme="dark"] .explain-accordion[open] .explain-summary{color:#818cf8;background:#1e2035;border-bottom-color:#252840}
        [data-theme="dark"] .explain-item{border-bottom-color:#1e2035}
        [data-theme="dark"] .explain-text{color:#8892b0}
        [data-theme="dark"] .explain-bullets{color:#8892b0}
        [data-theme="dark"] .body-text{color:#8892b0}
        [data-theme="dark"] .ref-link{color:#5b6280}
        [data-theme="dark"] .ref-link a{color:#818cf8}
        [data-theme="dark"] p.caption{color:#4a5070}
        [data-theme="dark"] #back-top{background:#4338ca;box-shadow:0 2px 8px rgba(67,56,202,.4)}
        [data-theme="dark"] #dark-toggle{background:#1e2035;border-color:#2e3154;color:#8892b0}
        [data-theme="dark"] #dark-toggle:hover{background:#252840;color:#d1d5e0}
        /* Dark scrollbars */
        [data-theme="dark"] *{scrollbar-color:#303450 transparent}
        [data-theme="dark"] ::-webkit-scrollbar-thumb{background:#303450}
        [data-theme="dark"] ::-webkit-scrollbar-thumb:hover{background:#4a5070}
        /* Colored chips */
        [data-theme="dark"] .chip-ok  {background:#052e16;border-color:#166534;color:#4ade80}
        [data-theme="dark"] .chip-warn{background:#1c1503;border-color:#713f12;color:#fbbf24}
        [data-theme="dark"] .chip-crit{background:#1f0a0a;border-color:#7f1d1d;color:#f87171}
        [data-theme="dark"] .hero[data-nav-level="2"] .chip     {background:#1e1c3a;border-color:#3730a3;color:#a5b4fc}
        [data-theme="dark"] .hero[data-nav-level="2"] .chip-ok  {background:#052e16;border-color:#166534;color:#4ade80}
        [data-theme="dark"] .hero[data-nav-level="2"] .chip-warn{background:#1c1503;border-color:#713f12;color:#fbbf24}
        [data-theme="dark"] .hero[data-nav-level="2"] .chip-crit{background:#1f0a0a;border-color:#7f1d1d;color:#f87171}
        /* Score badges */
        [data-theme="dark"] .badge-ok  {background:#052e16;color:#4ade80}
        [data-theme="dark"] .badge-warn{background:#1c1503;color:#fbbf24}
        [data-theme="dark"] .badge-crit{background:#1f0a0a;color:#f87171}
        /* Explain label + action */
        [data-theme="dark"] .explain-label{background:#2d2b5e;color:#a5b4fc}
        [data-theme="dark"] .explain-action{background:#052e16}
        [data-theme="dark"] .explain-action .explain-label{background:#14532d;color:#4ade80}
        /* Table severity/trend cells — brighter on dark */
        [data-theme="dark"] td.trend-up2{color:#f87171}
        [data-theme="dark"] td.trend-up {color:#fb923c}
        [data-theme="dark"] td.trend-dn {color:#4ade80}
        [data-theme="dark"] td.sev-crit {}
        [data-theme="dark"] td.sev-warn {}
        [data-theme="dark"] td.sev-info {}
        [data-theme="dark"] .sev-badge-crit{background:#2a1218;border-color:#7f1d1d;color:#fca5a5}
        [data-theme="dark"] .sev-badge-warn{background:#1f1a0c;border-color:#78350f;color:#fcd34d}
        [data-theme="dark"] .sev-badge-info{background:#17152e;border-color:#312e7a;color:#a5b4fc}
        /* kv helpers */
        [data-theme="dark"] .kv-arrow{color:#4a5070}
        [data-theme="dark"] .kv-delta{color:#5b6280}

        /* Severity bar dark */
        [data-theme="dark"] #sev-bar{border-bottom-color:#1e2035}
        [data-theme="dark"] .sev-pill-crit{background:#2a1218;border-color:#7f1d1d;color:#fca5a5}
        [data-theme="dark"] .sev-pill-warn{background:#1f1a0c;border-color:#78350f;color:#fcd34d}
        [data-theme="dark"] .sev-pill-info{background:#17152e;border-color:#312e7a;color:#a5b4fc}
        /* Expand bar dark */
        [data-theme="dark"] #expand-bar{background:#13151f;border-bottom-color:#252840;box-shadow:0 1px 4px rgba(0,0,0,.3)}
        [data-theme="dark"] .exp-btn{background:#1a1d2b;border-color:#2e3154;color:#8892b0}
        [data-theme="dark"] .exp-btn:hover{background:#2d2b5e;border-color:#4338ca;color:#a5b4fc}
        [data-theme="dark"] #exp-bar-label{color:#4a5070}
        [data-theme="dark"] #exp-bar-sep{background:#252840}
        /* CSV button dark */
        [data-theme="dark"] .tbl-csv-btn{background:#1a1d2b;border-color:#2e3154;color:#8892b0}
        [data-theme="dark"] .tbl-csv-btn:hover{background:#2d2b5e;border-color:#4338ca;color:#a5b4fc}
        /* Jump-to-critical dark */
        [data-theme="dark"] #jump-crit{background:#b91c1c;box-shadow:0 2px 8px rgba(185,28,28,.5)}
        [data-theme="dark"] #jump-crit:hover{background:#991b1b}
        /* Toggle button */
        #dark-toggle{display:flex;align-items:center;justify-content:center;margin-left:auto;width:24px;height:24px;border:1px solid #e5e7eb;border-radius:6px;background:#f9fafb;color:#6b7280;font-size:13px;cursor:pointer;flex-shrink:0;transition:background .12s,color .12s,border-color .12s;padding:0;line-height:1}
        #dark-toggle:hover{background:#f3f4f8;color:#1a1f2e}
        /* Call tree dark */
        [data-theme="dark"] .ctree-wrap{border-color:#252840}
        [data-theme="dark"] .ct-expbtn{background:#1a1d2b;border-color:#2e3154;color:#8892b0}
        [data-theme="dark"] .ct-expbtn:hover{background:#2d2b5e;border-color:#4338ca;color:#a5b4fc}
        [data-theme="dark"] .ctree-table thead th{background:#1a1d2b;color:#8892b0;border-bottom-color:#252840}
        [data-theme="dark"] .ctrow td{border-bottom-color:#1e2035;color:#c5cae0}
        [data-theme="dark"] .ctrow:hover td{background:#1e2242}
        [data-theme="dark"] .ct-hot2{background:#2a1218}
        [data-theme="dark"] .ct-hot1{background:#1f1a0c}
        [data-theme="dark"] .ct-cool{background:#13151f}
        [data-theme="dark"] .ct-method{color:#d1d5e0}
        [data-theme="dark"] .ct-mod{background:#1e2035;color:#5b6280}
        [data-theme="dark"] .ct-bar-bg{background:#1e2035}
        [data-theme="dark"] .ct-leaf{color:#2e3154}
        [data-theme="dark"] .ct-toggle{color:#818cf8}
        [data-theme="dark"] .ctw-cnt{color:#4a5070}
        /* Donut dark */
        [data-theme="dark"] .donut-ctr-top{fill:#d1d5e0}
        [data-theme="dark"] .donut-ctr-bot{fill:#5b6280}
        [data-theme="dark"] .donut-lbl{color:#8892b0}
        [data-theme="dark"] .spark-label{color:#8892b0}
        [data-theme="dark"] .donut-pct{color:#5b6280}
        /* Stacked bar dark */
        [data-theme="dark"] .sbar-track{background:#1e2035}
        [data-theme="dark"] .sbar-lbl{color:#8892b0}
        [data-theme="dark"] .sbar-val{color:#d1d5e0}
        [data-theme="dark"] .sbar-pct{color:#4a5070}
        /* Sparkline dark */
        [data-theme="dark"] .spark-stats b{color:#d1d5e0}
        [data-theme="dark"] .spark-label{color:#8892b0}
        [data-theme="dark"] .spark-stats{color:#5b6280}
        [data-theme="dark"] .spark-stats b{color:#d1d5e0}
        [data-theme="dark"] .spark-line{stroke:#818cf8}
        </style>
        </head>
        <body>
        <nav id="sidebar">
          <div class="nav-brand">
            <div class="nav-brand-icon">DD</div>
            <div class="nav-brand-text">
              <span class="nav-brand-title">Dump Detective</span>
              <span class="nav-brand-sub" title="Generated {GEN_DATETIME}">{APP_VERSION} · {GEN_TIME}</span>
            </div>
            <button id="dark-toggle" onclick="toggleDark()" title="Toggle dark/light mode"><span id="dark-icon">🌙</span></button>
          </div>
          <div class="nav-search-wrap">
            <input id="search-box" placeholder="Filter ..." oninput="filterNav(this.value)">
            <button id="search-clear" title="Clear search" onclick="clearNavSearch()">×</button>
          </div>
          <div id="sev-bar"></div>
          <div class="nav-inner">
            <div class="nav-section-label">Sections</div>
            <div id="nav-list"></div>
          </div>
        </nav>
        <div id="content">
        <div id="expand-bar"><span id="exp-bar-label">Sections:</span><div id="exp-bar-sep"></div><button class="exp-btn" onclick="expandAll()" title="Expand all sections">⊞ Expand All</button><button class="exp-btn" onclick="collapseAll()" title="Collapse all sections">⊟ Collapse All</button></div>
        <main id="report-root">
        """;

    const string Footer = """
        </main></div>
        <button id="back-top" title="Back to top" onclick="window.scrollTo({top:0,behavior:'smooth'})">↑</button>
        <button id="jump-crit" title="Jump to next critical alert" onclick="jumpCrit()">⚠</button>
        <script>
        /* ── Navigation builder ───────────────────────────────────────────────── */
        (function(){
          const nav  = document.getElementById('nav-list');
          const root = document.getElementById('report-root');
          function shortTitle(t){ return t.replace(/^Dump Detective\s*[—\-]\s*/i,'').replace(/^Per-Dump\s+/i,''); }

          let curL1Div = null;
          let subList  = null;
          let subToggle= null;
          let subCount = 0;

          root.querySelectorAll('.hero').forEach(function(h){
            const id    = h.id;
            const num   = id.slice(2);
            const raw   = h.querySelector('.hero-title')?.textContent?.trim() ?? '';
            const level = parseInt(h.dataset.navLevel ?? '1');

            if(level === 1){
              curL1Div = document.createElement('div');
              curL1Div.className = 'nav-chapter';
              subList = null; subToggle = null; subCount = 0;

              const titleA = document.createElement('a');
              titleA.href = '#' + id;
              titleA.className = 'nav-title';
              titleA.textContent = shortTitle(raw);
              titleA.title = raw;
              curL1Div.appendChild(titleA);

              const chBody = document.getElementById('chb' + num);
              if(chBody){
                chBody.querySelectorAll(':scope > .card').forEach(function(c){
                  const ca = document.createElement('a');
                  ca.href = '#' + c.id;
                  ca.className = 'nav-card';
                  const hdr = c.querySelector('.card-title');
                  if(hdr){ const cl = hdr.cloneNode(true); cl.querySelectorAll('.tip-wrap').forEach(function(e){e.remove();}); ca.textContent = cl.textContent.replace('▾','').trim(); }
                  else { ca.textContent = c.id; }
                  ca.title = ca.textContent;
                  curL1Div.appendChild(ca);
                });
              }
              nav.appendChild(curL1Div);

            } else if(level === 2){
              if(!curL1Div) return;

              if(!subList){
                subList = document.createElement('div');
                subList.className = 'nav-sub-chapters';

                subToggle = document.createElement('a');
                subToggle.className = 'nav-subreports';
                subToggle.textContent = '▸ Sub-reports';

                const sl = subList, st = subToggle;
                subToggle.onclick = function(e){
                  e.preventDefault();
                  const open = sl.classList.toggle('open');
                  st.textContent = (open ? '▾ ' : '▸ ') + 'Sub-reports (' + subCount + ')';
                };
                curL1Div.appendChild(subToggle);
                curL1Div.appendChild(subList);
              }

              const a = document.createElement('a');
              a.href = '#' + id;
              a.textContent = shortTitle(raw);
              a.title = raw;
              subList.appendChild(a);
              subCount++;
              subToggle.textContent = '▸ Sub-reports (' + subCount + ')';
            }
          });
        })();

        /* ── Nav filter ───────────────────────────────────────────────────────── */
        function filterNav(q){
          q = q.trim();
          const btn = document.getElementById('search-clear');
          if(btn) btn.classList.toggle('vis', q.length > 0);
          q = q.toLowerCase();
          const allLinks = document.querySelectorAll('#nav-list a');
          if(!q){
            allLinks.forEach(function(a){ a.style.display = ''; });
            document.querySelectorAll('.nav-sub-chapters').forEach(function(sc){ sc.style.display = ''; });
            document.querySelectorAll('.nav-subreports').forEach(function(t){ t.style.display = ''; });
            return;
          }
          allLinks.forEach(function(a){
            a.style.display = a.textContent.toLowerCase().includes(q) ? '' : 'none';
          });
          document.querySelectorAll('.nav-sub-chapters').forEach(function(sc){
            const anyVis = Array.from(sc.querySelectorAll('a')).some(function(a){ return a.style.display !== 'none'; });
            sc.style.display = anyVis ? 'block' : 'none';
            const toggle = sc.previousElementSibling;
            if(toggle && toggle.classList.contains('nav-subreports'))
              toggle.style.display = anyVis ? '' : 'none';
          });
        }
        function clearNavSearch(){
          const sb = document.getElementById('search-box');
          if(sb){ sb.value = ''; sb.focus(); filterNav(''); }
        }

        /* ── Active nav highlight on scroll ──────────────────────────────────── */
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

        /* ── Floating tooltips ────────────────────────────────────────────────── */
        (function(){
          var ftip = document.createElement('div');
          ftip.id = 'ftip';
          document.body.appendChild(ftip);
          window.showTip = function(el){
            var t = el.dataset.tip;
            if(!t) return;
            ftip.innerHTML = t;
            ftip.style.display = 'block';
            var r = el.getBoundingClientRect();
            var tw = ftip.offsetWidth;
            var th = ftip.offsetHeight;
            var x = r.right + 10;
            if(x + tw > window.innerWidth - 8) x = r.left - tw - 10;
            var y = r.top + r.height / 2 - th / 2;
            if(y < 8) y = 8;
            if(y + th > window.innerHeight - 8) y = window.innerHeight - th - 8;
            ftip.style.left = x + 'px';
            ftip.style.top  = y + 'px';
          };
          window.hideTip = function(){ ftip.style.display = 'none'; };
        })();

        /* ── Back-to-top button ───────────────────────────────────────────────── */
        (function(){
          const btn = document.getElementById('back-top');
          window.addEventListener('scroll', function(){
            btn.classList.toggle('vis', window.scrollY > 400);
          }, {passive:true});
        })();

        /* ── Dark mode toggle ────────────────────────────────────────────────── */
        (function(){
          var DARK = 'dark';
          var stored = localStorage.getItem('dd-theme');
          var prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
          if(stored === DARK || (!stored && prefersDark)) applyDark(true);
          function applyDark(on){
            document.documentElement.setAttribute('data-theme', on ? DARK : 'light');
            var icon  = document.getElementById('dark-icon');
            if(icon) icon.textContent = on ? '☀️' : '🌙';
          }
          window.toggleDark = function(){
            var isDark = document.documentElement.getAttribute('data-theme') === DARK;
            applyDark(!isDark);
            localStorage.setItem('dd-theme', isDark ? 'light' : DARK);
          };
        })();

        /* ── Card collapse ────────────────────────────────────────────────────── */
        function toggleCard(id){
          const el = document.getElementById(id);
          if(el) el.classList.toggle('collapsed');
        }

        /* ── Table sort ───────────────────────────────────────────────────────── */
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
              const m = s.match(/^([\d,.]+)\s*(B|KB|MB|GB|TB)$/i);
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

        /* ── Table filter / search ────────────────────────────────────────────── */
        function filterTable(tid){
          const q = (document.getElementById('ts' + tid)?.value ?? '').trim().toLowerCase();
          const tbl = document.getElementById('t' + tid);
          if(!tbl) return;
          const allRows = Array.from(tbl.tBodies[0].rows);
          const total = allRows.length;
          let vis = 0;
          allRows.forEach(function(r){
            const match = !q || r.textContent.toLowerCase().includes(q);
            r.style.display = match ? '' : 'none';
            if(match) vis++;
          });
          const rc = document.getElementById('rc' + tid);
          if(rc){
            if(!q) rc.textContent = total.toLocaleString() + ' rows';
            else rc.textContent = vis.toLocaleString() + ' of ' + total.toLocaleString() + ' rows';
          }
        }

        /* ── CSV export ───────────────────────────────────────────────────────── */
        window.exportCsv = function(tid){
          const tbl = document.getElementById('t' + tid);
          if(!tbl) return;
          const lines = [];
          lines.push(Array.from(tbl.querySelectorAll('thead th')).map(function(th){
            return '"' + th.textContent.replace(/[⇅↑↓]/g,'').trim().replace(/"/g,'""') + '"';
          }).join(','));
          Array.from(tbl.tBodies[0].rows).forEach(function(r){
            if(r.style.display === 'none') return;
            lines.push(Array.from(r.cells).map(function(c){
              return '"' + c.textContent.trim().replace(/"/g,'""') + '"';
            }).join(','));
          });
          const blob = new Blob([lines.join('\r\n')], {type:'text/csv'});
          const a = document.createElement('a');
          a.href = URL.createObjectURL(blob);
          a.download = 'export_t' + tid + '.csv';
          a.click();
          URL.revokeObjectURL(a.href);
        };

        /* ── Expand / Collapse all sections ──────────────────────────────────── */
        window.expandAll = function(){
          document.querySelectorAll('.card.collapsed').forEach(function(c){ c.classList.remove('collapsed'); });
        };
        window.collapseAll = function(){
          document.querySelectorAll('.card:not(.collapsed)').forEach(function(c){ c.classList.add('collapsed'); });
        };

        /* ── Alert navigation (severity bar pills + jump-to-critical button) ─── */
        (function(){
          const _idx = {};
          /* Expand any collapsed card ancestor so the element is visible */
          function ensureVisible(el){
            var p = el.parentElement;
            while(p){
              if(p.classList && p.classList.contains('card') && p.classList.contains('collapsed'))
                p.classList.remove('collapsed');
              p = p.parentElement;
            }
          }
          /* Collect elements matching sel; for badge spans inside tds use the td as scroll target */
          function gather(sel){
            return Array.from(document.querySelectorAll(sel)).map(function(el){
              return (el.tagName === 'SPAN' && el.closest('td')) ? el.closest('td') : el;
            });
          }
          window.jumpToAlert = function(sel){
            const items = gather(sel);
            if(!items.length) return;
            if(_idx[sel] === undefined) _idx[sel] = -1;
            _idx[sel] = (_idx[sel] + 1) % items.length;
            const el = items[_idx[sel]];
            ensureVisible(el);
            el.scrollIntoView({behavior:'smooth', block:'center'});
            el.style.outline = '2px solid currentColor';
            setTimeout(function(){ el.style.outline = ''; }, 1500);
          };
          /* Use badge spans as anchors — they exist for both alerts and table cells */
          const CRIT_SEL = '.alert-crit, .sev-badge-crit';
          const WARN_SEL = '.alert-warn, .sev-badge-warn';
          const INFO_SEL = '.alert-info, .sev-badge-info';
          window.jumpCrit = function(){ window.jumpToAlert(CRIT_SEL); };
          const jb = document.getElementById('jump-crit');
          if(jb && document.querySelector(CRIT_SEL)) jb.classList.add('vis');

          /* ── Severity summary bar ─────────────────────────────────────────── */
          const bar = document.getElementById('sev-bar');
          if(bar){
            const crits = document.querySelectorAll(CRIT_SEL).length;
            const warns = document.querySelectorAll(WARN_SEL).length;
            const infos = document.querySelectorAll(INFO_SEL).length;
            if(!crits && !warns && !infos){ bar.style.display='none'; }
            else {
              function dot(bg){ return '<span style="display:inline-block;width:6px;height:6px;border-radius:50%;background:'+bg+';flex-shrink:0"></span>'; }
              let html = '';
              if(crits) html += '<span class="sev-pill sev-pill-crit" onclick="jumpToAlert(\''+CRIT_SEL+'\')" title="'+crits+' critical — click to cycle">&#10007; '+crits+'</span>';
              if(warns) html += '<span class="sev-pill sev-pill-warn" onclick="jumpToAlert(\''+WARN_SEL+'\')" title="'+warns+' warnings — click to cycle">&#9888; '+warns+'</span>';
              if(infos) html += '<span class="sev-pill sev-pill-info" onclick="jumpToAlert(\''+INFO_SEL+'\')" title="'+infos+' info — click to cycle">&#8505; '+infos+'</span>';
              bar.innerHTML = html;
            }
          }
        })();

        /* ── Keyboard shortcuts ─────────────────────────────────────────────── */
        /* / = focus nav filter | j/k = next/prev section | t = next table      */
        (function(){
          let _sIdx = -1, _tIdx = -1;
          document.addEventListener('keydown', function(e){
            if(e.target.tagName==='INPUT'||e.target.tagName==='TEXTAREA'||e.target.isContentEditable) return;
            if(e.ctrlKey||e.metaKey||e.altKey) return;
            switch(e.key){
              case '/':{
                e.preventDefault();
                const sb = document.getElementById('search-box');
                if(sb){ sb.focus(); sb.select(); }
                break;
              }
              case 'j':{
                const ss = Array.from(document.querySelectorAll('.card'));
                if(!ss.length) break;
                _sIdx = Math.min(_sIdx+1, ss.length-1);
                ss[_sIdx].scrollIntoView({behavior:'smooth', block:'start'});
                break;
              }
              case 'k':{
                const ss = Array.from(document.querySelectorAll('.card'));
                if(!ss.length) break;
                _sIdx = Math.max(_sIdx-1, 0);
                ss[_sIdx].scrollIntoView({behavior:'smooth', block:'start'});
                break;
              }
              case 't':{
                const ts = Array.from(document.querySelectorAll('.table-wrap'));
                if(!ts.length) break;
                _tIdx = (_tIdx+1) % ts.length;
                ts[_tIdx].scrollIntoView({behavior:'smooth', block:'start'});
                break;
              }
            }
          });
        })();
        /* ── Call tree toggle ─────────────────────────────────────────────── */
        function ctToggle(rowId, btn){
          var row = document.getElementById(rowId);
          if(!row) return;
          var hidden = row.style.display === 'none' || row.style.display === '';
          row.style.display = hidden ? 'table-row' : 'none';
          btn.classList.toggle('open', hidden);
          btn.textContent = hidden ? '▼' : '▶';
        }
        function ctExpandAll(treeId){
          var wrap = document.getElementById(treeId);
          if(!wrap) return;
          wrap.querySelectorAll('.ct-children').forEach(function(r){ r.style.display='table-row'; });
          wrap.querySelectorAll('.ct-toggle').forEach(function(b){ b.classList.add('open'); b.textContent='▼'; });
        }
        function ctCollapseAll(treeId){
          var wrap = document.getElementById(treeId);
          if(!wrap) return;
          wrap.querySelectorAll('.ct-children').forEach(function(r){ r.style.display='none'; });
          wrap.querySelectorAll('.ct-toggle').forEach(function(b){ b.classList.remove('open'); b.textContent='▶'; });
        }
        </script>
        </body></html>
        """;
}
