# diff

**Category:** Replay / Comparison  
**Included in `analyze --full`:** No (replay only)

## What it does

Compares two saved report files (`.json` or `.bin`) produced by DumpDetective and produces a diff report highlighting changes. No dump files are required — all data comes from the saved reports.

---

## How it works

1. **Load both reports**: `TrendRawSerializer.Load(beforePath)` + `TrendRawSerializer.Load(afterPath)` — each returns a `DumpSnapshot[]` or a single `ReportDoc`
2. **Match chapters and sections** by name between before and after reports
3. **Diff tables**: for each table, matches rows by the key column (default: column 0 — usually type name or label). For each matched row, compares column values. Changed cells are rendered as `new ← old`. New rows (in after but not before) and deleted rows (in before but not after) are highlighted with different colors.
4. **Diff alerts**: matches alerts by title. Level and detail changes are highlighted.
5. **Emit output**: builds a `ReportDoc` containing the diff sections and replays through the target-format `IRenderSink`

---

## Input formats

Both before and after files can be:
- `report` — produced by `analyze --full -o before.bin`
- `trend-raw` — produced by `trend-analysis -o week1.bin`

---

## Options

| Option | Description |
|---|---|
| `<before>` | Path to the "before" `.json` or `.bin` file (required) |
| `<after>` | Path to the "after" `.json` or `.bin` file (required) |
| `--command <name>` | Diff only the named command's chapter(s) (repeatable) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`); default: `before-after.html` |

---

## Example usage

```
# Full diff of two analyze --full runs
DumpDetective analyze before.dmp --full -o before.bin
DumpDetective analyze after.dmp  --full -o after.bin
DumpDetective diff before.bin after.bin -o delta.html

# Diff two trend runs (compare this week vs last week)
DumpDetective diff week1.bin week2.bin -o trend-delta.html

# Diff only the memory-leak chapter
DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html
```

---

## What to look for

| Signal | What it means |
|---|---|
| **Object count growing (new > old)** | A type's instance count increased between captures — active leak in progress. The rate of growth is visible from the delta value. |
| **New alert in after, absent in before** | A configured threshold was crossed between the two captures. Read the alert text for which metric breached what limit. |
| **Alert severity upgraded** | `Info → Warning → Critical` progression confirms the condition is worsening over time. |
| **Thread count increased significantly** | Thread leak or sustained load increase. Check `thread-pool` and `async-stacks` for pool saturation. |
| **LOH / fragmentation ratio growing** | Memory fragmentation accumulating. Correlate with `large-objects` to see if allocation patterns changed. |
| **String duplicate count growing** | More duplicate strings accumulating — usually correlates with request count growth, but an unusually high per-request rate indicates a string-creation issue. |
| **No changes at all** | Two identical captures or the diff scope is too narrow (`--command` filtering to sections that didn't change). |
