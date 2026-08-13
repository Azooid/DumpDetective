# loh-trace

**Category:** Trace — GC and Memory  
**Included in `trace-analyze`:** Yes

## What it does

Tracks Large Object Heap (LOH) committed size across GC collections to detect steady-state growth and fragmentation trends. The LOH is only compacted when `GCSettings.LargeObjectHeapCompactionMode` is set — without compaction, free holes accumulate indefinitely.

---

## Analyzer: `LohTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Reads `GCHeapStats` events after each GC to extract LOH `GenerationSize3` and `TotalPromotedSize3` values. Plots LOH size over the trace timeline and computes the growth rate (bytes per second). A linear regression over the size samples is used to project how long until LOH reaches a threshold. LOH fragmentation is estimated from the ratio of promoted vs. surviving bytes.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| LOH growing monotonically | Objects allocated on LOH (≥ 85 KB) not being collected |
| High promoted bytes but flat LOH size | LOH is being collected but fragmentation may be growing |
| LOH growth correlating with request rate | Per-request LOH allocations — check for unPooled `byte[]` or `string` operations |
