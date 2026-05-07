# connection-pool

**Category:** Infrastructure / Network  
**Included in `analyze --full`:** Yes

## What it does

Finds all live ADO.NET and ORM database connection and command objects on the heap. Shows the connection state, masked connection string, and in-flight SQL command text. Identifies connection leaks, pool exhaustion, and long-running queries.

---

## Analyzer: `ConnectionPoolAnalyzer`

**Implements:** `IHeapObjectConsumer` directly  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### Recognized types

Two sets of type names are matched: connection types (`SqlConnection`, `OleDbConnection`, `OdbcConnection`, `NpgsqlConnection`, `MySqlConnection`, `OracleConnection` for both `System.Data.*` and provider namespaces) and command types (`SqlCommand`, `OleDbCommand`, `OdbcCommand`, `NpgsqlCommand`, `MySqlCommand`, `Microsoft.EntityFrameworkCore.Storage.RelationalCommand`). `HeapTypeMeta.IsConnection` and `HeapTypeMeta.IsDbCommand` flags are set per MethodTable by `HeapWalker.BuildMeta`.

### How it works

For connection objects, the state field is read by trying `_state`, `_connectionState`, and `_objectState` in order, mapping the integer to a label: 0 = `Closed`, 1 = `Open`, 16 = `Connecting`, 32 = `Executing`, 64 = `Fetching`, 256 = `Broken`. The connection string is read by trying `_connectionString`, `ConnectionString`, and `_userConnectionOptions`; credentials are masked before storing by replacing `Password=[^;]+` and `User Id=[^;]+` with `***` using a regex.

For command objects, `_commandText`, `_text`, and `CommandText` are tried in order. Non-empty results are trimmed and stored.

All field reads are wrapped in try/catch. Clone/merge: each parallel clone collects its own connection and command lists; `MergeFrom` appends them.

---

## Pre-warm path

`ConnectionPoolAnalyzer` itself is the consumer in `DumpCollector.CollectHeapObjectsCombined` — the full detail analyzer (state, connection string masking, command text reads) runs directly during the combined walk. Populates `ConnectionPoolData` in the session cache. Subsequent calls in the same session (`analyze --full` + `connection-pool` running in parallel) get the result instantly.

---

## Consumer: `LightweightStatsConsumer` (count only)

**Class**: `DumpDetective.Analysis.Memory.Consumers.LightweightStatsConsumer`  
**Walk**: Lightweight scan via `HeapObjectCollector.CollectHeapObjects`  
**Purpose**: Counts live connection objects (no state read, no connection string masking) for `DumpSnapshot.DbConnectionCount`, which feeds health scoring. The `LightweightStatsConsumer` also accumulates `TimerCount`, `WcfCount`, `WcfFaulted`, and `EventLeakTotals` in the same object; all are summed in `MergeFrom`.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on connection string or command text |
| `--state <name>` | Filter by state (e.g. `Open`, `Executing`, `Broken`) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **High count of `Open` connections** | Connections opened but not returned to pool — missing `using` / `await using` or exception paths that skip `Close()` |
| **Connections in `Broken` state** | Network failure left connections in an unrecoverable state; they hold pool slots but can't be used. Pool eventually exhausts. |
| **Many `Executing` or `Fetching`** | Long-running queries at dump time. Read the command text to identify which queries. |
| **Total connection count > pool max (default 100)** | Pool is exhausted; new requests are queuing or throwing `InvalidOperationException: Timeout expired` |
| **Identical command text repeated many times** | N+1 query pattern — same query running once per item in a loop |
| **`RelationalCommand` objects in EF Core** | EF Core commands in flight; can identify unfinished queries if `_commandText` is populated |
| **Very large connection string count** | Multiple different databases/environments — verify all point to correct targets |

---

## Typical output shape

```
Connection Pool — 524 connections, 312 commands

State        Type                                     ConnStr                    Count
Open         SqlConnection                            Server=db01;DB=Orders;***   487
Executing    SqlConnection                            Server=db01;DB=Orders;***    31
Broken       SqlConnection                            Server=db01;DB=Orders;***     6

Top SQL Commands
  SELECT [o].[Id], [o].[CustomerId], ... FROM [Orders] AS [o]  (289×)
  UPDATE [Inventory] SET [Qty] = @p0 WHERE [Id] = @p1          (18×)
  EXEC usp_GetOrderLines @OrderId = @p0                         (7×)
```
