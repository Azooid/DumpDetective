# async-trace

**Category:** Trace — Threads & Concurrency  
**Included in `trace-analyze`:** Yes

## What it does

Parses TPL (Task Parallel Library) lifecycle events from a `.nettrace` or `.etl` trace to detect async/await anti-patterns and Task scheduling pressure:

- **Sync-over-async** — calls to `.Wait()`, `.Result`, or `GetAwaiter().GetResult()` on a `Task`; detected when a `Task/Schedule` event is followed immediately by a thread blocking on that Task from the same call site.
- **Long-running continuations** — Tasks whose execution time from `Task/Execute` to `Task/Completed` exceeds the slow threshold; these hold a thread-pool thread for an extended period, reducing throughput.
- **Continuation scheduling sites** — user-code frames that schedule the most Task continuations; high scheduling frequency from a single site often indicates an over-decomposed async pipeline or excessive `Task.WhenAll` fan-out.

---

## Collecting a trace

TPL events require `DotNETRuntimePrivate` keyword `0x40` (Tasks):

### dotnet-trace

```bash
# TPL events only
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x40:5' \
  -p <pid>

# TPL + CPU sampling (recommended — correlates async overhead with CPU cost)
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0x40:5' \
  -p <pid>
```

### PerfView

```bash
PerfView.exe /ClrEvents:Tasks,Default /NoGui collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <N>` | Top N items per section (default: 20) |
| `--process <name>` | Filter to a specific process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Report sections

| Section | Content |
|---|---|
| **Summary** | Total Tasks scheduled, completed, sync-over-async instances detected, long-running count |
| **Sync-over-async sites** | Call sites where `.Wait()`/`.Result` was detected — count and call frame |
| **Long-running continuations** | Tasks that held a thread-pool thread for an extended period |
| **Top continuation schedulers** | User-code frames scheduling the most continuations |

---

## What to look for

| Signal | What it means |
|---|---|
| **Any sync-over-async site** | Deadlock risk under ASP.NET / thread-pool exhaustion; always use `await` instead of `.Wait()`/`.Result` |
| **Long-running continuations on thread-pool threads** | Blocks a thread-pool thread; use `Task.Run` to offload CPU-bound work, or `ConfigureAwait(false)` to avoid capturing the synchronization context |
| **High continuation count from one scheduler site** | Over-decomposition — consider batching or restructuring the async pipeline to reduce Task overhead |
| **Spike in Task scheduling rate correlating with request bursts** | Task fan-out proportional to load — check for unbounded `Task.WhenAll` on large collections |
| **Many Tasks scheduled but low completion rate** | Tasks may be queued but not executing — possible thread-pool starvation; cross-reference with `threadpool-starvation` |
