# load

**Category:** Cache Lifecycle  
**Included in `analyze --full`:** No (standalone cache-build command)

## What it does

Pre-builds all analysis caches for a dump file (or every dump in a directory) so that subsequent `analyze --full` and individual command runs complete in seconds rather than minutes. Without `load`, caches are built on demand during the first full analysis — but `load` lets you front-load that cost and validate success before running analysis.

---

## Cache steps (in order)

| # | Cache file | Built by | Description |
|---|---|---|---|
| 1 | `stringGroups.bin` + in-memory `HeapSnapshot` | `HeapWalker` + all 10 consumers | Full parallel heap walk: type stats, inbound refs, string groups, gen counters, exceptions, async state machines, timers, WCF state, connections, event handlers |
| 2 | `heap-fragmentation.bin` | `FragmentationConsumer` via `HeapWalker` | Live/free bytes per segment, free-hole histogram |
| 3 | `<dumpname>.bfs.idx` | `BfsIndexBuilder` (3-pass) | Forward-reference graph (Brotli-compressed). Pass 1: count refs per object. Pass 2: allocate index. Pass 3: write edges. Used by `static-refs`, `high-refs`, `memory-leak`, `type-instances` for exact retained sizes. |
| 4 | `<dumpname>.parent.map` | Derived from BFS pass | Child→parent address map (disk-backed `DiskBackedParentMap`). Used by root-chain tracing in `memory-leak`. |
| 5 | `hot-addr-types.bin` | `InboundRefConsumer` results | Referencing-type breakdown for top-30 most-referenced objects. Used by `high-refs` for source-type display. |
| 6 | `gc-roots.bin` | Handle + stack enumeration | GC root address → kind+type map. Used by `gc-roots` fast path. |
| 7 | `static-roots.bin` | Static field enumeration | Statically-rooted object addresses + declaring type info. Used by `static-refs`. |
| 8 | `finalizer-queue.bin` | Finalizer queue scan | Per-type stats from `EnumerateFinalizableObjects()`. Used by `finalizer-queue`. |
| 9 | `event-analysis.bin` | `EventLeakConsumer` results | Subscriber counts per `(publisher type, field)` pair. Used by `event-analysis`. |

All files are written to `.ddcache/<dump-name>/` directory alongside the dump.

---

## Cache validation

Each cache file stores a header: `Magic(4) + Version(int,4) + DumpLength(long,8) + DumpLastWriteTicks(long,8)`. On load, the header is compared against the current dump file's length and last-write time. Mismatch = cache is stale and must be rebuilt.

---

## Algorithm — step by step

1. Resolves dump path(s) — single file or all `.dmp`/`.mdmp` files in directory, sorted alphabetically.
2. For each dump:
   - Checks each cache step's file for validity
   - Skips valid caches unless `--force` is set
   - Runs missing steps in order with a `RunStatus(label, action)` spinner per step
   - Displays elapsed time per step on completion
3. In directory mode: shows `[N/total]` prefix before each dump's name.

---

## Options

| Option | Description |
|---|---|
| `<path>` | Path to a `.dmp` / `.mdmp` file, or a directory containing them |
| `--force, -f` | Rebuild all caches even if valid ones already exist |

---

## What to expect after running

- All 9 steps show a checkmark with elapsed time
- Largest files: `<name>.bfs.idx` (500 MB–3 GB depending on heap size), `<name>.parent.map` (200 MB–1 GB)
- `analyze --full` that previously took 20–30 minutes will complete in 30–120 seconds after full `load`
- If a step fails (logged with error), that command will re-run the analysis on demand at next use

---

## Example usage

```
# Cache a single dump (standard use)
DumpDetective load app.dmp

# Cache all dumps in a directory
DumpDetective load C:\dumps\

# Force rebuild all caches (e.g. after upgrading DumpDetective)
DumpDetective load app.dmp --force

# Invalidate and rebuild in two steps
DumpDetective close app.dmp && DumpDetective load app.dmp
```
