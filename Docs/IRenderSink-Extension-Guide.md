# Adding a New Method to `IRenderSink`

This document is a step-by-step checklist for extending the output pipeline with a new visual element type. Follow **every** step — missing one will silently break either the HTML output, the `.bin`/`.json` replay formats, or the text fallback formats.

---

## Overview of the pipeline

```
Command code
   │  calls sink.MyNewChart(...)
   ▼
IRenderSink           ← interface contract + default text fallback
   ├── HtmlSink       ← full HTML/SVG render
   ├── CaptureSink    ← captures to ReportElement subtype  ─┐
   ├── BinSink        ── delegates to CaptureSink           │  replay chain
   ├── JsonSink       ── delegates to CaptureSink           │
   ├── MarkdownSink   ← GFM table / text representation     │
   └── TextSink       ← ASCII table / text representation   │
                                                            │
ReportDoc model       ← immutable document tree  ◄──────────┘
   └── ReportElement subtype  (e.g. ReportMyNewChart)

CoreJsonContext       ← AOT JSON source-gen registration

ReportDocReplay       ← deserialises ReportElement → calls sink method again
```

---

## Files to edit (in order)

| # | File | What to add |
|---|------|-------------|
| 1 | `DumpDetective.Core/Models/ReportDoc.cs` | New model class(es) + `[JsonDerivedType]` |
| 2 | `DumpDetective.Core/Json/CoreJsonContext.cs` | `[JsonSerializable]` for every new class |
| 3 | `DumpDetective.Core/Interfaces/IRenderSink.cs` | New interface method with default text fallback |
| 4 | `DumpDetective.Reporting/Templates/HtmlSink.css` | CSS classes for the HTML render |
| 5 | `DumpDetective.Reporting/Sinks/HtmlSink.cs` | Full HTML/SVG implementation |
| 6 | `DumpDetective.Reporting/Sinks/CaptureSink.cs` | Capture call into `ReportElement` |
| 7 | `DumpDetective.Reporting/Sinks/BinSink.cs` | Delegate to `_capture` |
| 8 | `DumpDetective.Reporting/Sinks/JsonSink.cs` | Delegate to `_capture` |
| 9 | `DumpDetective.Reporting/Sinks/MarkdownSink.cs` | GFM table or text render |
| 10 | `DumpDetective.Reporting/Sinks/TextSink.cs` | ASCII table or text render |
| 11 | `DumpDetective.Reporting/ReportDocReplay.cs` | New `case` in the replay switch |

> **Why BinSink and JsonSink matter**: they don't write directly — they delegate every call to an internal `CaptureSink`. If you skip adding the forwarding method, the interface default is invoked instead, which calls simpler methods like `Sparkline()` or `KeyValues()`. The `.bin`/`.json` file then stores the wrong element type, and replaying it with `render report.bin --output report.html` silently loses your new chart.

---

## Step-by-step

### Step 1 — `ReportDoc.cs`

Add your model class(es) **before** or **after** the existing `Report*` classes:

```csharp
// Supporting data type (if needed)
public sealed class ReportMyNewChartItem
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
}

// The element itself — must extend ReportElement
public sealed class ReportMyNewChart : ReportElement
{
    public List<ReportMyNewChartItem> Items { get; set; } = [];
    public string? Caption { get; set; }
}
```

Then register it in the `[JsonDerivedType]` attribute block on `ReportElement`:

```csharp
[JsonDerivedType(typeof(ReportMyNewChart), "myNewChart")]
```

The string discriminator (`"myNewChart"`) is written into the JSON `$type` field and is how `ReportDocReplay` identifies the element on replay. Pick a **camelCase** name and never change it once a `.bin`/`.json` file has been saved.

---

### Step 2 — `CoreJsonContext.cs`

Add one `[JsonSerializable]` attribute for **every** new class (including supporting types):

```csharp
[JsonSerializable(typeof(ReportMyNewChart))]
[JsonSerializable(typeof(ReportMyNewChartItem))]
```

This is required because the project uses `PublishAot=true`. Missing this causes a runtime crash (AOT) or a `NotSupportedException` (non-AOT Debug builds) the first time `JsonSerializer` touches the type.

---

### Step 3 — `IRenderSink.cs`

Add the interface method with a **default implementation** that produces a readable fallback for sinks that do not override it (currently `ConsoleSink` and any future sinks):

```csharp
/// <summary>
/// One-line description of what this renders.
/// HTML renders as XYZ; other sinks fall back to a plain text representation.
/// </summary>
void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
{
    // Default: plain text fallback used by ConsoleSink and any unimplemented sinks
    foreach (var item in items)
        Text($"{item.Label}: {item.Value:F1}");
}
```

Rules:
- All parameters should be nullable (`?`) or have defaults so callers can be added incrementally.
- The default body must never be empty — it should produce at least a `Text()` or `KeyValues()` call so output is readable in all formats.

---

### Step 4 — `HtmlSink.css`

Add CSS classes used by your HTML render. Keep them prefixed with a short namespace that matches the element (e.g. `mchart-` for "myNewChart"):

```css
/* ── MyNewChart ─────────────────────────────────────────── */
.mchart-card     { background: var(--card-bg); border-radius: 10px; padding: 1rem; margin: .75rem 0; }
.mchart-row      { display: flex; align-items: center; gap: .5rem; margin: .3rem 0; }
.mchart-lbl      { width: 200px; font-size: 12px; color: var(--text-muted); }
/* ... */

[data-theme="dark"] .mchart-card { /* dark overrides */ }
```

Add the dark-mode block directly after the light-mode block.

---

### Step 5 — `HtmlSink.cs`

Add a `public void MyNewChart(...)` method. Use `_sb` (the `StringBuilder`) to emit inline HTML/SVG:

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
{
    if (items.Count == 0) return;
    _sb.Append("<div class=\"mchart-card\">");
    if (caption is not null)
        _sb.Append($"<p class=\"chart-caption\">{HtmlEncode(caption)}</p>");
    foreach (var item in items)
    {
        _sb.Append($"<div class=\"mchart-row\">");
        _sb.Append($"<span class=\"mchart-lbl\">{HtmlEncode(item.Label)}</span>");
        _sb.Append($"<span>{item.Value:F1}</span>");
        _sb.Append("</div>");
    }
    _sb.Append("</div>");
}
```

Helper available in `HtmlSink`: `HtmlEncode(string s)` — use it for any user-supplied string that goes into element content or attribute values.

---

### Step 6 — `CaptureSink.cs`

Add an explicit implementation that stores the call as a `ReportMyNewChart` element:

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
    => CurrentElements().Add(new ReportMyNewChart
    {
        Items   = items.ToList(),
        Caption = caption,
    });
```

`CurrentElements()` returns the element list for the current open section. Do **not** call `base.MyNewChart(...)` here — that would emit text instead of capturing the structured element.

---

### Step 7 — `BinSink.cs`

Add a forwarding method after the other delegating methods:

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
    => _capture.MyNewChart(items, caption);
```

---

### Step 8 — `JsonSink.cs`

Identical forwarding (same pattern as `BinSink`):

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
    => _capture.MyNewChart(items, caption);
```

---

### Step 9 — `MarkdownSink.cs`

Write a GFM (GitHub-Flavored Markdown) representation. A table is usually the best choice:

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
{
    if (caption is not null) { _w.WriteLine($"*{caption}*"); _w.WriteLine(); }
    _w.WriteLine("| Label | Value |");
    _w.WriteLine("|-------|------:|");
    foreach (var item in items)
        _w.WriteLine($"| {E(item.Label)} | {item.Value:F1} |");
    _w.WriteLine();
}
```

Use the existing `E(string s)` private helper for all string values (it escapes `|` and backticks).

---

### Step 10 — `TextSink.cs`

Write a plain-text / ASCII representation. Column-aligned output is preferred:

```csharp
public void MyNewChart(
    IReadOnlyList<ReportMyNewChartItem> items,
    string? caption = null)
{
    if (caption is not null) _w.WriteLine($"  {caption}");
    foreach (var item in items)
        _w.WriteLine($"  {item.Label,-40}  {item.Value,10:F1}");
    _w.WriteLine();
}
```

---

### Step 11 — `ReportDocReplay.cs`

Add a `case` to the `switch` inside `ReplayElements` (or equivalent method). Insert it **near the other chart-type cases** to keep the file organized:

```csharp
case ReportMyNewChart mnc:
    sink.MyNewChart(
        mnc.Items,
        mnc.Caption);
    break;
```

The switch is on the `ReportElement` base type. The `[JsonDerivedType]` attribute registered in Step 1 ensures the correct subtype is deserialized from JSON.

---

## Checklist summary

Copy and tick off:

- [ ] `ReportDoc.cs` — model class + `[JsonDerivedType]`
- [ ] `CoreJsonContext.cs` — `[JsonSerializable]` for every new class
- [ ] `IRenderSink.cs` — interface method with default fallback
- [ ] `HtmlSink.css` — CSS classes (light + dark)
- [ ] `HtmlSink.cs` — HTML/SVG render
- [ ] `CaptureSink.cs` — capture to `ReportElement`
- [ ] `BinSink.cs` — forward to `_capture`
- [ ] `JsonSink.cs` — forward to `_capture`
- [ ] `MarkdownSink.cs` — GFM table or text
- [ ] `TextSink.cs` — ASCII table or text
- [ ] `ReportDocReplay.cs` — replay `case`

---

## What about `ConsoleSink`?

`ConsoleSink` renders to the terminal via Spectre.Console. It does **not** need an explicit implementation for chart methods — the default body defined in `IRenderSink` (Step 3) handles it by falling back to `Text()` / `KeyValues()` calls, which `ConsoleSink` already handles. Only add an explicit override if you want terminal-specific rendering (e.g. a Spectre progress bar for a gauge).

---

## Example: the two chart types added in this session

Both `MultiSparkline` and `CompareBar` followed this exact checklist:

| Method | IRenderSink default | HTML render | CaptureSink element | Replay case |
|---|---|---|---|---|
| `MultiSparkline` | One `Sparkline()` call per series | Stacked synchronized SVG lines with fill gradient | `ReportMultiSparkline` | `case ReportMultiSparkline ms:` |
| `CompareBar` | `KeyValues()` with "A vs B" format | Dual normalized horizontal bars (indigo + green) | `ReportCompareBar` | `case ReportCompareBar cb:` |
