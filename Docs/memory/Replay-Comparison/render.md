# render

**Category:** Replay / Comparison  
**Included in `analyze --full`:** No (replay only)

## What it does

Converts a previously saved DumpDetective JSON or BIN report file to any output format without re-analyzing a dump. Useful for re-exporting in a different format, extracting a subset of chapters, or re-rendering with a different trend baseline.

---

## How it works

The `render` command reads a saved `ReportDoc` structure from a `.json` or `.bin` file and replays it through an `IRenderSink` of the target format:

1. **Load**: `TrendRawSerializer.Load(inputPath)` — reads `.json` or auto-decompresses `.bin` (Brotli) → deserializes `DumpSnapshot[]` or a single `ReportDoc` using AOT JSON source-gen context
2. **Identify report type**:
   - Single-command report (from `analyze dump.dmp -o report.bin`)
   - Full-analyze report (from `analyze dump.dmp --full -o full.bin`)
   - Trend-raw file (from `trend-analysis *.dmp -o snapshots.bin`)
3. **Apply filters**: `--from N` extracts one dump's report; `--command name` extracts specific chapters; `--mini` suppresses per-dump sub-reports in trend files
4. **Replay**: `ReportDocReplay.Replay(doc, sink)` — iterates `ReportDoc.Chapters`, `Chapter.Sections`, `Section.Elements`; dispatches each element type (table row, alert, header, etc.) to the sink's methods
5. **Write output**: sink produces `html`/`md`/`txt`/`json`/`bin` to the output path

---

## Input formats

| Extension | Contents |
|---|---|
| `.json` | Plain UTF-8 JSON — `DumpSnapshot[]` or single `ReportDoc` |
| `.bin` | Brotli-compressed JSON — same structure, auto-detected by magic header |

---

## Options

| Option | Description |
|---|---|
| `<input>` | Path to a `.json` or `.bin` file (required) |
| `--baseline <n>` | 1-based index of the dump to use as trend baseline (trend-raw only; default: 1) |
| `--ignore-event <type>` | Exclude publisher types matching this substring from event-analysis tables (repeatable) |
| `--mini` | Render trend summary only — suppress all per-dump sub-report chapters |
| `--from <n>` | Extract dump #N's full sub-report as a standalone output (1-based) |
| `--command <name>` | Include only the named command's chapter(s) (repeatable) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`, `.bin`); default: `<input>.html` |

---

## Example usage

```
# Re-render saved trend report as HTML
DumpDetective render snapshots.bin

# Render as Markdown
DumpDetective render snapshots.bin -o report.md

# Render only trend summary (suppress per-dump detail)
DumpDetective render snapshots.bin --mini -o trend-only.html

# Use dump 2 as baseline for delta coloring
DumpDetective render snapshots.bin --baseline 2 -o report-baseline2.html

# Extract dump #4's full report as standalone HTML
DumpDetective render snapshots.bin --from 4 -o d4-full.html

# Extract only memory-leak chapters across all dumps
DumpDetective render snapshots.bin --command memory-leak -o memleak-only.html

# Extract memory-leak + heap-stats for dump #4 only
DumpDetective render snapshots.bin --from 4 --command memory-leak --command heap-stats -o d4-subset.html
```

---

## What to look for

- Use `--mini` when the full per-dump detail is too large for a browser — trend files across 10+ large dumps can produce 200+ MB of HTML
- Use `--from N --command <name>` to create focused single-topic reports for sharing with team members without sending the full multi-dump file
- Use `--baseline N` when the first dump is not a clean "before" state — e.g. when dump #1 already shows the problem and you want delta coloring relative to a post-restart capture
