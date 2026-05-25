# DumpDetective Memory Guide

Complete standalone reference for memory-dump workflows and commands. For the full documentation index, see [documentation.md](documentation.md).

---

## Scope

Input file types: `.dmp`, `.mdmp`, Linux ELF core dumps (`.core`, no extension)

> **Linux / .NET Core 8 dumps** — dumps captured via `dotnet-dump collect` on Linux, or ELF core dumps from containerized workloads, are supported from v3.3.0. Open them the same way as Windows dumps; DumpDetective detects the format automatically.

Primary use cases:

- Memory growth and retention analysis
- GC pressure and fragmentation
- Finalizer, handle, and async backlog diagnostics
- Thread and deadlock inspection from a dump snapshot
- Cross-dump trend analysis and report replay/diff

---

## Fast Start

```bash
# Full incident report
DumpDetective analyze app.dmp --full

# Save replayable output (recommended)
DumpDetective analyze app.dmp --full --output report.bin

# Re-render without reopening dump
DumpDetective render report.bin --output report.html
```

---

## Output and Replay Model

- Omit `-o` to get `<dump-name>.html` alongside the dump.
- `.html` — self-contained with inline CSS/JS, sticky nav, and collapsible sections.
- `.bin` — Brotli-compressed JSON; compact for archival and replayable via `render`.
- `render` converts any saved `.json`/`.bin` into `.html`, `.md`, `.txt`, `.json`, or `.bin` without reopening the dump.
- `diff` compares two saved reports without reopening dumps.

---

## Command Index

Commands marked **No** are excluded from `analyze --full` because they require extra arguments or are too slow for batch runs.

| Command | Category | In `--full` | Detail |
|---|---|:---:|---|
| `analyze` | Orchestration | — | [→](memory/Orchestrator/analyze.md) |
| `trend-analysis` | Orchestration | — | [→](memory/Orchestrator/trend-analysis.md) |
| `render` | Replay | — | [→](memory/Replay-Comparison/render.md) |
| `diff` | Replay | — | [→](memory/Replay-Comparison/diff.md) |
| `load` | Cache Lifecycle | — | [→](memory/Cache-Lifecycle/load.md) |
| `close` | Cache Lifecycle | — | [→](memory/Cache-Lifecycle/close.md) |
| `heap-stats` | Heap / Memory | ✓ | [→](memory/Heap-Memory/heap-stats.md) |
| `gen-summary` | Heap / Memory | ✓ | [→](memory/Heap-Memory/gen-summary.md) |
| `heap-fragmentation` | Heap / Memory | ✓ | [→](memory/Heap-Memory/heap-fragmentation.md) |
| `large-objects` | Heap / Memory | ✓ | [→](memory/Heap-Memory/large-objects.md) |
| `pinned-objects` | Heap / Memory | ✓ | [→](memory/Heap-Memory/pinned-objects.md) |
| `memory-leak` | Heap / Memory | ✓ | [→](memory/Heap-Memory/memory-leak.md) |
| `high-refs` | Heap / Memory | ✓ | [→](memory/Heap-Memory/high-refs.md) |
| `string-duplicates` | Heap / Memory | ✓ | [→](memory/Heap-Memory/string-duplicates.md) |
| `finalizer-queue` | Heap / Memory | ✓ | [→](memory/Heap-Memory/finalizer-queue.md) |
| `handle-table` | Heap / Memory | ✓ | [→](memory/Heap-Memory/handle-table.md) |
| `static-refs` | Heap / Memory | ✓ | [→](memory/Heap-Memory/static-refs.md) |
| `weak-refs` | Heap / Memory | ✓ | [→](memory/Heap-Memory/weak-refs.md) |
| `event-analysis` | Heap / Memory | ✓ | [→](memory/Heap-Memory/event-analysis.md) |
| `exception-analysis` | Exceptions | ✓ | [→](memory/Exceptions-Diagnostics/exception-analysis.md) |
| `gc-roots` | Heap / Memory | No | [→](memory/Heap-Memory/gc-roots.md) |
| `thread-analysis` | Threads | ✓ | [→](memory/Threads-Concurrency/thread-analysis.md) |
| `thread-pool` | Threads | ✓ | [→](memory/Threads-Concurrency/thread-pool.md) |
| `deadlock-detection` | Threads | ✓ | [→](memory/Threads-Concurrency/deadlock-detection.md) |
| `async-stacks` | Threads | ✓ | [→](memory/Threads-Concurrency/async-stacks.md) |
| `connection-pool` | Infrastructure | ✓ | [→](memory/Infrastructure-Network/connection-pool.md) |
| `http-requests` | Infrastructure | ✓ | [→](memory/Infrastructure-Network/http-requests.md) |
| `timer-leaks` | Infrastructure | ✓ | [→](memory/Infrastructure-Network/timer-leaks.md) |
| `wcf-channels` | Infrastructure | ✓ | [→](memory/Infrastructure-Network/wcf-channels.md) |
| `module-list` | Targeted | ✓ | [→](memory/Targeted-Interactive/module-list.md) |
| `type-instances` | Targeted | No | [→](memory/Targeted-Interactive/type-instances.md) |
| `object-inspect` | Targeted | No | [→](memory/Targeted-Interactive/object-inspect.md) |
| `load` | Cache Lifecycle | No | Pre-builds BFS index and parent map on disk |

---

## Orchestration Commands

### `analyze`

Produces a scored health report for one dump. In default mode, runs a fast lightweight collection and scores the dump across nine health dimensions. With `--full`, runs all included sub-commands and embeds their reports as chapters.

```bash
DumpDetective analyze app.dmp
DumpDetective analyze app.dmp --full
DumpDetective analyze app.dmp --full --output report.html
DumpDetective analyze app.dmp --full --output report.bin
```

Options: `--full`, `--str-top <n>`, `--str-min-count <n>`, `--str-min-waste <bytes>`, `--bfs-depth <n>`, `--exact`, `-o <file>` — [full details →](memory/Orchestrator/analyze.md)

### Health Scoring

`HealthScorer.Score` evaluates the dump snapshot against thresholds from `dd-thresholds.json`. Default critical thresholds: Gen2 > 5 GB, LOH > 1 GB, finalizer thread blocked, deadlock cycle detected, thread count > 1000, event subscribers > 50,000, active connections > 500, async backlog > 10,000. Score: 0–100 (lower = worse).

### `trend-analysis`

Analyzes multiple dumps over time, producing a trend table of heap size, generation distribution, object counts, thread counts, and other metrics per dump. Confirms whether a leak is actively growing.

```bash
DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
DumpDetective trend-analysis C:\dumps --full --output trend.bin
DumpDetective trend-analysis --list dumps.txt --baseline 2 --output report.md
```

Options: explicit paths, `<directory>`, `--list <file>`, `--full`, `--baseline <n>`, `--ignore-event <type>`, `--prefix <p>`, `--str-top <n>`, `-o <file>` — [full details →](memory/Orchestrator/trend-analysis.md)

---

## Replay and Comparison Commands

### `render`

Converts a saved `.json`/`.bin` report to any output format without reopening the dump. Supports format conversion, chapter filtering, trend baseline changes, and per-dump sub-report extraction.

```bash
DumpDetective render snapshots.bin
DumpDetective render snapshots.bin --output report.md
DumpDetective render snapshots.bin --mini --output trend-only.html
DumpDetective render snapshots.bin --from 2 --command memory-leak --output d2-memleak.html
```

Options: `--baseline <n>`, `--ignore-event <type>`, `--mini`, `--from <n>`, `--command <name>`, `-o <file>` — [full details →](memory/Replay-Comparison/render.md)

### `diff`

Compares two saved reports. Matches chapters and sections by name, rows by key column. Changed cells appear as `new ← old`; new/deleted rows are highlighted.

```bash
DumpDetective diff before.bin after.bin -o delta.html
DumpDetective diff week1.bin week2.bin --changed-only -o delta.html
DumpDetective diff week1.bin week2.bin --command memory-leak -o memleak-delta.html
```

Options: `--key-col <n>`, `--changed-only`, `--show-same`, `--command <name>`, `--ignore-event <type>`, `-o <file>` — [full details →](memory/Replay-Comparison/diff.md)

---

## Cache Lifecycle Commands

### `load`

Pre-builds all analysis caches (BFS index, parent map, fragmentation, GC roots, static roots, finalizer queue, event analysis, string groups) so that `analyze --full` runs in seconds rather than minutes.

```bash
DumpDetective load app.dmp
DumpDetective load C:\dumps\
DumpDetective load app.dmp --force
```

Options: `<path>` (file or directory), `--force` — [full details →](memory/Cache-Lifecycle/load.md)

### `close`

Deletes `.ddcache/<dump-name>/` for one dump or all dumps in a directory.

```bash
DumpDetective close app.dmp
DumpDetective close app.dmp --dry-run
DumpDetective close C:\dumps\
```

Options: `<path>`, `--dry-run` — [full details →](memory/Cache-Lifecycle/close.md)

---

## Heap and Memory Commands

### `heap-stats`
Top types by total size and instance count. Options: `--top <n>`, `--sort size|count|name`, `--min-size <bytes>`, `--filter <str>`, `--gen gen0|gen1|gen2|loh|poh` — [full details →](memory/Heap-Memory/heap-stats.md)

### `gen-summary`
Heap bytes and object counts by GC generation. No extra options. — [full details →](memory/Heap-Memory/gen-summary.md)

### `heap-fragmentation`
Free-hole analysis per heap segment; live/free byte ratio. — [full details →](memory/Heap-Memory/heap-fragmentation.md)

### `large-objects`
Individual objects ≥ 85 KB on the LOH. Options: `--top <n>`, `--min-size <bytes>`, `--filter <name>`, `--addresses`, `--type-breakdown` — [full details →](memory/Heap-Memory/large-objects.md)

### `pinned-objects`
Objects pinned via GC handles grouped by type and handle kind. Options: `--addresses` — [full details →](memory/Heap-Memory/pinned-objects.md)

### `memory-leak`
Suspects ranked by instance count and retained size with root-chain traces. Options: `--top <n>`, `--min-count <n>`, `--no-root-trace`, `--include-system` — [full details →](memory/Heap-Memory/memory-leak.md)

### `high-refs`
Most-referenced objects by inbound reference count. Options: `--top <n>`, `--min-refs <n>`, `--addresses` — [full details →](memory/Heap-Memory/high-refs.md)

### `string-duplicates`
Duplicate string groups sorted by wasted bytes. Options: `--top <n>`, `--min-count <n>`, `--min-waste <bytes>`, `--pattern <str>` — [full details →](memory/Heap-Memory/string-duplicates.md)

### `finalizer-queue`
Types queued for finalization; resurrection detection; finalizer-blocked detection. Options: `--top <n>`, `--addresses` — [full details →](memory/Heap-Memory/finalizer-queue.md)

### `handle-table`
GC handle table by kind: Strong, WeakShort, WeakLong, Pinned, AsyncPinned, Dependent. Options: `--top <n>`, `--filter <kind>` — [full details →](memory/Heap-Memory/handle-table.md)

### `static-refs`
Statically-rooted object trees with optional BFS retained-size computation. Options: `--filter <t>`, `--exclude <t>`, `--addresses`, `--bfs-depth <n>` — [full details →](memory/Heap-Memory/static-refs.md)

### `weak-refs`
Weak GC handles — alive vs. collected object breakdown. Options: `--addresses` — [full details →](memory/Heap-Memory/weak-refs.md)

### `event-analysis`
Event fields with high subscriber counts; event-handler leak detection. Options: `--top <n>` — [full details →](memory/Heap-Memory/event-analysis.md)

### `gc-roots`
Traces GC root paths for a type or specific object address. Expensive — not in `--full`. Options: `--type <name>`, `--address <0xADDR>`, `--max-results <n>`, `--no-indirect` — [full details →](memory/Heap-Memory/gc-roots.md)

---

## Exceptions and Diagnostics

### `exception-analysis`
Live exception instances by type; message samples and stack frames. Options: `--top <n>`, `--filter <t>`, `--addresses`, `--stack` — [full details →](memory/Exceptions-Diagnostics/exception-analysis.md)

---

## Threads and Concurrency Commands

### `thread-analysis`
All managed threads by state, wait kind, GC mode, and top stack frames. Options: `--stacks`, `--blocked-only`, `--state <s>`, `--name <substr>` — [full details →](memory/Threads-Concurrency/thread-analysis.md)

### `thread-pool`
ThreadPool counters: worker counts, pending items, task state breakdown. — [full details →](memory/Threads-Concurrency/thread-pool.md)

### `deadlock-detection`
Sync-block DFS cycle detection; reports thread cycle with lock addresses. — [full details →](memory/Threads-Concurrency/deadlock-detection.md)

### `async-stacks`
Active async state machines by method name and suspension state. Options: `--filter <t>`, `--top <n>`, `--addresses` — [full details →](memory/Threads-Concurrency/async-stacks.md)

---

## Infrastructure and Network Commands

### `connection-pool`
Live DB connections and commands by state; masked connection strings. Options: `--addresses` — [full details →](memory/Infrastructure-Network/connection-pool.md)

### `http-requests`
In-flight `HttpRequestMessage` / `HttpWebRequest` / `HttpClient` objects by method and URL, plus outbound connection pool (`ServicePoint`) visibility showing active connections and limits per endpoint. URIs are resolved on both .NET Core and .NET Framework dumps. Options: `--addresses` — [full details →](memory/Infrastructure-Network/http-requests.md)

### `timer-leaks`
Live `System.Threading.Timer` instances; due-time, period, and callback. Callback method names are fully resolved (including two-hop `.NET Framework` navigation `Timer → TimerHolder → TimerQueueTimer`) and decoded from CLR compiler-generated notation (lambdas, closures, async state machines). Options: `--addresses` — [full details →](memory/Infrastructure-Network/timer-leaks.md)

### `wcf-channels`
WCF channel state (0–5 → CommunicationState); endpoint addresses; fault reasons. Distinguishes client proxy channels from server-side WCF hosting and configuration infrastructure to avoid false positives. Options: `--addresses` — [full details →](memory/Infrastructure-Network/wcf-channels.md)

---

## Targeted and Interactive Commands

### `type-instances`
All instances of a specific type with individual and retained sizes. Options: `--type <name>` (required), `--top <n>`, `--addresses`, `--min-size <bytes>`, `--gen <gen>` — [full details →](memory/Targeted-Interactive/type-instances.md)

### `object-inspect`
Deep recursive field dump for one object by hex address. Options: `--address <hex>` (required), `--depth <n>`, `--max-array <n>`, `--retained`, `--retained-cap <n>`, `--no-cache`, `--no-save` — [full details →](memory/Targeted-Interactive/object-inspect.md)

### `module-list`
Loaded assemblies classified as Dynamic, GAC, System, or App. Options: `--filter <t>`, `--app-only` — [full details →](memory/Targeted-Interactive/module-list.md)

## Suggested Incident Triage Path

1. Run `analyze --full` and open the HTML output.
2. Read the health score and Critical/Warning findings first.
3. Follow up with `memory-leak`, `high-refs`, `heap-fragmentation`, `finalizer-queue`.
4. Use `gc-roots`, `type-instances`, and `object-inspect` to narrow specific suspects.
5. Save `.bin` snapshots over time and compare with `diff` to confirm growth rate.
