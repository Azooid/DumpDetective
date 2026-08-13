# socket-trace

**Category:** Trace — Network and I/O  
**Included in `trace-analyze`:** Yes

## What it does

Measures TCP connection latency, failures, and top remote hosts from `System.Net.Sockets` events. Identifies slow connect operations and connection error patterns.

---

## Analyzer: `SocketTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `System.Net.Sockets` event source events: `ConnectStart` / `ConnectStop`, `AcceptStart` / `AcceptStop`, and error events. Matches start/stop pairs by socket ID. For each connection: remote endpoint, latency, and error code (if any) are recorded. Results are grouped by remote host, sorted by average connect latency descending.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <n>` | Top remote hosts to show (default: `20`) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| High connect latency to one host | DNS resolution slow or remote host overloaded |
| High error rate (ECONNREFUSED) | Service down or wrong port |
| Many short-lived connections to same host | Missing connection pooling or keep-alive |
