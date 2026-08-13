# cache-patterns

**Category:** Retention / Leak Signals  
**Included in `analyze --full`:** Yes

## What it does

Scans the managed heap for `Dictionary<,>`, `ConcurrentDictionary<,>`, `MemoryCache`, `IMemoryCache`, `HashSet<>`, `List<>`, and similar collection types. Reads the entry count per instance and flags any collection whose entry count exceeds a configurable threshold as a potential unbounded cache. Results are grouped by declaring type and sorted by entry count descending.

This is the fastest way to find unbounded caches without tracing: if a `Dictionary` has 500,000 entries and is statically rooted, it is almost certainly the leak.

---

## Analyzer: `CachePatternsAnalyzer`

**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

Reads `HeapSnapshot.TypeStats` to find candidate collection types, then walks instances of those types using `heap.EnumerateObjectsOfType`. For each instance it reads the `_count` or `_size` field (varies by collection type) via `ClrObject.ReadValueTypeField`. Instances above the threshold are recorded with: full type name, entry count, estimated managed size, and declaring object chain (one level of referrer, resolved from `HeapSnapshot.InboundCounts`).

### Consumer: `CachePatternsConsumer`

Participates in the shared heap walk (`HeapWalker`). Accumulates counts per MethodTable so the analyzer can quickly skip types with zero collection instances.

---

## Options

| Option | Description |
|---|---|
| `<dump>` | Path to `.dmp` or `.mdmp` file |
| `--threshold <n>` | Entry count above which a collection is flagged (default: `10000`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |
| `-h, --help` | Show this help |

---

## What to look for

| Signal | What it means |
|---|---|
| `Dictionary` with 100k+ entries | Likely unbounded cache with no eviction policy |
| Multiple instances of the same type all near the threshold | Shared pool or per-tenant caching that scales with load |
| `MemoryCache` with very high entry count | SizeLimit not set or too high |
| Entry count growing across successive snapshots (`diff`) | Active leak — objects are being added but not removed |
