# string-duplicates

**Category:** Heap / Memory  
**Included in `analyze --full`:** Yes

## What it does

Finds duplicate `System.String` values on the managed heap — multiple string objects holding identical character content — and reports the wasted memory that could be saved by interning or deduplication. The report groups strings by value, showing the instance count, total bytes consumed by all copies, and the wasted bytes (total minus the cost of one copy). Groups are sorted by wasted bytes descending.

After rendering, the backing string dictionary (typically 1–2 GB) is released immediately by calling `snapshot.ReleaseStringGroups()`. This makes `string-duplicates` one of the few commands that actively shrinks memory usage after it runs.

---

## Analyzer: `StringDuplicatesAnalyzer`

**Class type:** Not `IHeapObjectConsumer` — reads `HeapSnapshot.StringGroups`  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

When `ctx.Snapshot.StringGroups` is available (always the case after `load` or `analyze --full`), the analyzer reads it directly — no heap traversal. It sorts entries by wasted bytes descending, applies `--top` and `--min` filters, and renders the result. For the slow path (no snapshot), it enumerates `heap.EnumerateObjects()` and filters for `System.String` objects, applying the same grouping logic inline.

## Consumer: `StringGroupConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.StringGroupConsumer`  
**Implements**: `IHeapObjectConsumer`; `IsThreadSafe = true` (no cloning — 256-stripe locking)  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`

### Key design: 256-stripe locking

The string dictionary is one of the most memory-intensive data structures in the tool — potentially 2–4 GB for a heap with 30M unique string values. Cloning it 8 times for parallel workers is not feasible. Instead, `StringGroupConsumer` uses the same 256-stripe locking pattern as `InboundRefConsumer`: 256 independent `Dictionary<string, StringGroupStats>` buckets, each protected by its own lock.

Stripe selection uses a Fibonacci hash of the first character XOR'd with the string length: `((uint)val[0] * 2654435761u) ^ (uint)val.Length) & 0xFF`. This avoids the clustering that a simple first-character hash would cause — without it, ~90% of real-world strings would fall into ~26 ASCII-letter stripes.

Strings longer than 512 characters are read via `obj.AsString(maxLength: 512)` — a deliberate cap that prevents reads of pathologically long strings from dominating memory. Strings truncated at 512 characters are grouped by their 512-char prefix, which may merge some distinct long strings. This is an acceptable trade-off for memory safety.

### Post-walk merge

After the parallel walk completes, `OnWalkComplete()` merges all 256 stripe dictionaries into a single `Dictionary<string, StringGroupStats>`, freeing each stripe as it is consumed. The merged result is stored as `HeapSnapshot.StringGroups`. This final merge is the peak memory moment — both the per-stripe dictionaries and the merged result briefly co-exist — but only one full copy is in memory at a time because each stripe is cleared before the next is processed.

---

## Pre-warm path

`StringGroupConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. Results stored in `ctx.Snapshot.StringGroups`. `StringDuplicatesAnalyzer` just reads from there — no second walk.

---

## Disk cache

None. Data comes from `HeapSnapshot` (freed after rendering to reclaim memory).

---

## Options

| Option | Description |
|---|---|
| `--top <n>` | Show top N duplicate groups by wasted bytes (default: 50) |
| `--min <count>` | Only show groups with ≥ n duplicate instances |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **High total "wasted bytes"** | String interning or a deduplication pass would materially reduce memory. If > 500 MB wasted, investigate the top groups. |
| **URL strings / request paths duplicated thousands of times** | Each request stores its own copy of the URL string. Use `string.Intern()` or a shared constant; or store as `Uri` and share the instance. |
| **Short identifier strings (status codes, roles, method names) duplicated** | Replace with `enum` or a shared constant pool. |
| **JSON property name strings repeated** | `System.Text.Json` with `JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve` may avoid repeated property name allocations. |
| **`""` (empty string) with large count** | Expected — many objects initialize string fields to `""`. Use `string.Empty` reference for slight sharing benefit. Not a real issue. |
| **Very long strings (512-char bucket) at top** | The 512-char prefix matches many different full strings — consider whether these large strings can be streamed instead of materialized. |

---

## Typical output shape

```
String Duplicates — 28,441,992 strings  •  3.1 GB total  •  ~1.8 GB potentially wasted

Value (truncated to 80 chars)           Count    Total Size   Wasted
"application/json"                     891,441     156 MB     156 MB
"GET"                                  441,882      77 MB      77 MB
"User"                                 222,112      42 MB      42 MB
"https://api.example.com/v1/orders"    111,881      38 MB      38 MB
""                                   2,884,112      50 MB      50 MB
```
