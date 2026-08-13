# memory-pressure

**Category:** Heap Overview  
**Included in `analyze --full`:** Yes

## What it does

Provides a unified view of managed memory at the time of the dump:

- Heap committed and reserved bytes by segment kind (Ephemeral, LOH, POH)
- Generation sizes (Gen0 / Gen1 / Gen2 / LOH / POH) and object counts
- Thread stack memory (`StackBase − StackLimit` per alive thread)
- Fragmentation percentage per segment kind
- Alerts for high fragmentation or excessive thread stack consumption

This is the fastest single command to understand the overall memory topology of a process.

---

## Analyzer: `MemoryPressureAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Reads `ClrRuntime.Heap.Segments` for segment-level committed and reserved byte counts. Generation sizes are read from `ClrRuntime.Heap.GenerationTable` (accurate even on Server GC heaps with multiple heap segments). Thread stack consumption is computed by iterating `ClrRuntime.Threads` and reading `StackBase` and `StackLimit` for each live thread — the difference is the stack committed range.

Fragmentation per segment kind is computed as: `(freeBytes / committedBytes) × 100`. A separate pass reads free objects (type name `Free`) from the already-built `TypeStats` snapshot to get total free bytes by generation.

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## What to look for

| Signal | What it means |
|---|---|
| LOH fragmentation > 30% | Large objects are leaving holes; consider `GCSettings.LargeObjectHeapCompactionMode` |
| Gen2 >> Gen0 + Gen1 | Long-lived object accumulation; look for static roots or long-running caches |
| Thread stack total > 1 GB | Too many threads; consider ThreadPool or reducing stack size |
| POH (Pinned Object Heap) growing | Excessive pinning; check `pinned-objects` |
