# connection-pool-trace

**Category:** Trace — SQL and Serialization  
**Included in `trace-analyze`:** Yes

## What it does

Tracks database connection open/close events from `Microsoft.Data.SqlClient.EventSource` to detect connection pool leaks, pool exhaustion, and peak concurrency. Reports the timeline of open connections, maximum concurrent connections, and any periods where connection count was at the pool limit.

---

## Analyzer: `ConnectionPoolTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses `ConnectionOpened`, `ConnectionClosed`, `ConnectionPoolCreated`, and `ConnectionPoolExhausted` events. Tracks open connection count over time by matching open/close pairs by connection ID. Pool exhaustion events (where a caller had to wait for a connection) are flagged with wait time. Connection strings are masked for security.

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## What to look for

| Signal | What it means |
|---|---|
| Monotonically increasing open count | Connection leak — `SqlConnection` not being disposed |
| Pool exhaustion events | Peak concurrency exceeds `MaxPoolSize`; increase limit or reduce connection hold time |
| Long open-to-close duration | Connection held across slow operations; consider reducing scope |
| Connections opened per request correlates with request rate | Expected — baseline behavior; watch for non-linear growth |
