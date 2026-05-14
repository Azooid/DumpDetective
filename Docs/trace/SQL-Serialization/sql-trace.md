# sql-trace

**Category:** Trace — SQL & Serialization  
**Included in `trace-analyze`:** Yes

## What it does

Parses ADO.NET and Entity Framework Core SQL command events from a `.nettrace` or `.etl` trace to measure database query performance. Reports aggregate execution time per query pattern, flags slow individual commands, counts errors, and shows per-database breakdown and a duration timeline.

Supported providers:
- `Microsoft-AdoNet-SystemData` — classic `System.Data.SqlClient` and ADO.NET (`BeginExecute`/`EndExecute`)
- `Microsoft.Data.SqlClient.EventSource` — modern `Microsoft.Data.SqlClient`
- `System.Data.SqlClient.EventSource` — legacy .NET Framework client
- `Microsoft-EntityFrameworkCore` — EF Core command events

---

## Query selection — three tiers

Up to **170 unique query patterns** are selected per report:

| Tier | Count | Ranked by |
|---|---|---|
| 1 | 100 (default `--top`) | Cumulative total execution time |
| 2 | 50 (`top / 2`) | Worst single-execution time — surfaces one-off slow outliers |
| 3 | 20 additional | Unique patterns with real SQL text not already in tiers 1–2 |

All three tiers are merged and deduplicated. Final sort order:

1. Real SQL text queries — total time ↓, then max single execution ↓
2. `(no SQL text, ...)` placeholders at the bottom (commands where no SQL text could be captured)

---

## Collecting a trace

### dotnet-trace (.nettrace)

```bash
dotnet trace collect \
  --providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4,
               System.Data.SqlClient.EventSource:0xFF:4,
               Microsoft-EntityFrameworkCore:0xFFFF:5' \
  -p <pid>
```

Add CPU sampling to correlate SQL time with CPU cost:

```bash
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft.Data.SqlClient.EventSource:0xFF:4,
               System.Data.SqlClient.EventSource:0xFF:4,
               Microsoft-EntityFrameworkCore:0xFFFF:5' \
  -p <pid>
```

### PerfView (ETL)

```bash
PerfView.exe /Providers:"Microsoft.Data.SqlClient.EventSource,System.Data.SqlClient.EventSource,Microsoft-EntityFrameworkCore" /NoGui collect
```

> **Tip:** If you see many `(no SQL text)` entries, the process may be using stored procedures or parameterized queries whose text is not emitted by the provider at the configured verbosity. Try increasing the provider level to `5` (Verbose).

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <N>` | Tier-1 row cap (default: 100; tier-2 = N/2; 0 = unlimited) |
| `--process <name>` | Filter to a specific process name |
| `--slow-ms <ms>` | Slow command threshold in milliseconds (default: 500) |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Report sections

| Section | Content |
|---|---|
| **Summary** | Total commands, error count, avg/max duration, slow command count |
| **Slow Commands** | Individual executions above `--slow-ms`, sorted by duration |
| **Top Queries by Execution Time** | Deduplicated patterns — count, total, max, avg, errors, SQL text |
| **By Database** | Per-database totals: command count, total time, avg time |
| **Duration Timeline** | Per-second cumulative SQL time — reveals bursty vs. sustained load |

---

## What to look for

| Signal | What it means |
|---|---|
| **High total time on one query pattern** | A hot query running frequently — candidate for indexing or caching |
| **MaxMs >> AvgMs** | Occasional outlier — check for parameter sniffing, lock waits, or cold-cache execution plans |
| **High error count** | Connection failures, timeouts, or constraint violations — check SQL Server logs |
| **Slow commands all from the same DB** | Single database bottleneck — may indicate connection pool exhaustion or insufficient read replicas |
| **Many `(no SQL text)` entries** | Provider verbosity too low, or stored-procedure calls whose text is never emitted |
| **Bursty timeline** | SQL load is spiky — may correlate with request bursts seen in `http-trace` |
| **AvgMs > 100 ms for high-frequency queries** | Query pattern running hundreds of times — small latency improvement has high aggregate impact |
