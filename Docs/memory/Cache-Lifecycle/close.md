# close

**Category:** Cache Lifecycle  
**Included in `analyze --full`:** No (standalone cache-cleanup command)

## What it does

Deletes all analysis cache files created by `load` or `analyze --full` for a dump file or an entire directory of dumps. Frees disk space occupied by the `.ddcache/<dump-name>/` directory.

---

## What is deleted

The entire `.ddcache/<dump-name>/` directory alongside each dump. Contents:

| File | Max size | Built by |
|---|---|---|
| `<name>.bfs.idx` | 500 MB – 3 GB | `BfsIndexBuilder` (forward-reference graph, Brotli-compressed) |
| `<name>.parent.map` | 200 MB – 1 GB | Derived from BFS (child→parent disk-backed map) |
| `heap-fragmentation.bin` | 1–10 MB | `FragmentationConsumer` |
| `finalizer-queue.bin` | < 1 MB | Finalizer queue scan |
| `event-analysis.bin` | < 1 MB | `EventLeakConsumer` |
| `stringGroups.bin` | 10–500 MB | `StringGroupConsumer` |
| `hot-addr-types.bin` | < 1 MB | Inbound-ref top-30 breakdown |
| `gc-roots.bin` | 1–10 MB | GC root enumeration |
| `static-roots.bin` | < 1 MB | Static field enumeration |

---

## Options

| Option | Description |
|---|---|
| `<path>` | Path to a `.dmp` / `.mdmp` file, or a directory containing dumps |
| `--dry-run` | Show what would be deleted without actually deleting anything |

---

## When to use

- **Disk space pressure**: BFS index files alone can be 1–3 GB per large dump. `close` reclaims that space when analysis is complete.
- **After upgrading DumpDetective**: cache format version changes may invalidate existing caches. Run `close` then `load` to rebuild cleanly. (The cache header version check will detect stale files, but `close` + `load` is cleaner.)
- **After replacing a dump file**: if a new dump is captured with the same name, the old cache will be stale. The version header check catches this, but explicit `close` + `load` avoids any edge cases.
- **CI/CD pipelines**: run `close` after automated analysis to keep build agents clean.

---

## Example usage

```
# Remove caches for a single dump
DumpDetective close app.dmp

# Preview what would be deleted (safe)
DumpDetective close app.dmp --dry-run

# Remove caches for all dumps in a directory
DumpDetective close C:\dumps\

# Full rebuild: delete caches then reload
DumpDetective close app.dmp && DumpDetective load app.dmp

# Force rebuild in one step (skips valid caches unless forced)
DumpDetective load app.dmp --force
```
