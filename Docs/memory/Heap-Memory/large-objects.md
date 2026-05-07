# large-objects

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Lists objects at or above a configurable size threshold (default: 85,000 bytes — the LOH promotion boundary), sorted by size descending. It also reports LOH segment totals: committed bytes, live bytes, free bytes, and fragmentation ratio.

The 85 KB default is deliberate: objects at or above this size are promoted directly to the Large Object Heap, bypassing the normal generational promotion process. Because the LOH is not compacted by default, frequent large-object allocations leave persistent holes that accumulate over time. This command makes those allocations visible.

---

## Analyzer: `LargeObjectsAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — walks specific segments directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### How it works

When `--min-size` is at or above 85,000 bytes (the default), only LOH (`GCSegmentKind.Large`) and POH (`GCSegmentKind.Pinned`) segments are enumerated. These segments are accessed directly via `seg.EnumerateObjects()`, bypassing all of Gen0, Gen1, and Gen2. This makes the scan 5–10× faster than a full heap walk for the common case. If `--min-size` is set below 85,000 bytes, a full `heap.EnumerateObjects()` pass is required to find large objects in any generation.

For each qualifying object, the analyzer reads its type name and, if it is an array type, the element type name via `obj.Type.ComponentType?.Name`. The segment kind (LOH, POH, Gen2) is resolved from the segment containing the object's address and recorded in each result row.

A second LOH-only pass then computes the fragmentation summary: for each LOH segment, it sums live object sizes and free-hole sizes separately to calculate committed, live, free, and fragmentation percentage.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--min-size <bytes>` | Minimum object size threshold (default: 85000) |
| `--filter <text>` | Substring filter on type name |
| `--top <n>` | Show top N objects by size (default: 100) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Very large `System.Byte[]`** | Large buffers (request bodies, response streams, file reads) allocated directly. Use `ArrayPool<byte>` or `MemoryPool<byte>` for buffers that frequently allocate/release. |
| **Large `System.String`** | Huge in-memory strings (serialized JSON bodies, SQL results loaded as string). Consider streaming parsers (`JsonDocument`, `Utf8JsonReader`). |
| **Many objects just above 85 KB** | Frequent LOH allocations. Even a slight size reduction (e.g., 85,000 → 84,000 bytes) keeps objects in Gen2, enabling compaction and more frequent collection. |
| **LOH fragmentation > 30%** | Persistent holes in LOH from allocate/free cycles. Not compacted by default — requires `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` before a Gen2 GC, or process restart. |
| **`System.Object[]` large** | Large arrays of objects. Examine with `object-inspect` to see what the elements are. |
| **`ElemType = System.Char`** | Large `char[]` — usually backing a large `StringBuilder` or `MemoryStream` with string data. |

---

## Typical output shape

```
Large Objects (≥ 85 KB) — 9,112 objects  •  2.8 GB total

Type                      Element Type   Size       Segment   Address
System.Byte[]             System.Byte    892 MB      LOH      0xFFFF1234
System.Byte[]             System.Byte    440 MB      LOH      0xFFFE4421
System.String             —               12 MB      LOH      0xFFFC1188
System.Object[]           System.Object    9 MB      LOH      0xFFFB2200
MyApp.Models.DataBuffer   —                2 MB      POH      0xFFF91100

LOH Summary
  Committed: 2.9 GB   Live: 2.1 GB   Free: 0.8 GB   Fragmentation: 28%
```
