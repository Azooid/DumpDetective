# file-io-trace

**Category:** Trace — Network and I/O  
**Included in `trace-analyze`:** ETL only (requires kernel file events)

## What it does

Detects slow synchronous file reads/writes and high-throughput files from kernel file I/O events. Synchronous file I/O on the thread pool is a common starvation source: a single slow disk operation blocks a pool thread for the duration.

**Requires an ETL trace.** EventPipe (`.nettrace`) does not expose kernel file events. Collect with PerfView or `wpr`.

---

## Analyzer: `FileIoTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses Windows kernel `FileIo/Read`, `FileIo/Write`, and `FileIo/Create` events. Matches read/write operations by I/O request packet (IRP) ID. For each I/O: file path, bytes transferred, latency, and thread ID are recorded. Operations above the P95 latency are flagged as slow. Results are grouped by file path, sorted by total bytes transferred.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.etl` file (ETL only) |
| `--top <n>` | Top files to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Thread pool thread doing slow file I/O | Synchronous I/O; switch to `FileStream` with `useAsync: true` |
| Same file written by many threads | Lock contention on file handle |
| Large reads from temp path | Buffering to disk (e.g., `System.IO.MemoryMappedFiles` or temp file caching) |
