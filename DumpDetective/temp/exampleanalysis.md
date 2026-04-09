# Memory Leak Analysis Report — Excellon FW5 / IIS ASP.NET — Scenario 2

**Date:** April 7, 2026
**Tool:** DumpDetective.exe (+ dotnet-dump v9.0.661903 for supplementary counts)
**Process:** w3wp.exe (IIS App Pool: `BALLOADTEST`, PID: 4672)
**Framework:** Excellon FW5 ERP — Entity Framework 4.x/5.x (ObjectContext style), WCF, DevExpress.Web

---

## Executive Summary

Three memory dumps were captured across a single-day test cycle on **April 6, 2026**. DumpDetective analysis reveals a **partially-healed but still actively leaking** process compared to reference Scenario 1:

- **Managed heap grew from 39.69 MB at baseline to 1.04 GB under load**, never returning to baseline. After ~5¾ hours of cooldown it stabilised at **695.77 MB** — still 17× baseline.
- **57,314 live event subscriptions** across **186 distinct event types** are rooted in memory at peak load (D2) — the highest-impact single leak category. The top two compiler-generated classes alone account for **51,346 instances** (89.6%): `DisplayClass32_0` (31,958 across two generic variants) + `DisplayClass35_0` (19,388).
- **Sessions are fully recovered at the object level.** Under load, **133 `Session` objects** were live (confirmed by `dotnet-dump dumpheap`). After 5h45m cooldown, **0 `Session` objects remain** — all were collected. However, **3 orphaned `SessionStore` event subscriptions** persist at D3, meaning 3 per-session stores outlived their parent `Session` wrappers and were not properly disposed.
- **Finalize queue exploded to 124,678** — driven overwhelmingly by `System.Data.DataColumn` (113,684) and `System.Data.DataTable` (2,153) that are queued for finalization but not yet freed, confirming undisposed EF/DataSet roots.
- **InstallBaseData (1,388 instances) shows absolute zero recovery** between load and cooldown — the static anchor for this cache remains unfixed.
- **4 distinct categories of string duplication** are identified in D2 and D3 that represent cacheable business data being allocated redundantly (e.g., `"Service Manager 3W"` duplicated 64,896 times, wasting 3.84 MB).
- **9 `TdsParserStateObject` handles are rooted as strong GC roots** at cooldown — indicating SqlConnections that were never closed and returned to pool.

---

## 1. Dump Timeline

| Dump | Label | File Size | Time | Threads (Total / Alive) | Description |
|------|-------|-----------|------|------------------------|-------------|
| D1 | `dd_D1_11_44AM.txt` | 714 MB | 11:44 AM | 31 / 31 | **Baseline** — no user sessions active |
| D2 | `dd_D2_12_58PM.txt` | 3,334 MB | 12:58 PM | 88 / 81 | **Under load** — +1h14m from D1, **133 sessions** (dotnet-dump: 133 `Session` objects; DumpDetective: 135 `Principal` event delegates) |
| D3 | `dd_D3_06_43PM.txt` | 2,697 MB | 6:43 PM | 107 / 41 | **Extended cooldown** — +5h45m from D2 |

> **Thread note:** D3 has 107 total threads but only 41 alive — 66 dead threads are still tracked by the CLR, indicating thread pool churn throughout the day. `ThreadLocal<T>` instances attached to these dead threads are pending finalization, explaining the elevated finalize queue at D3 versus D1 (24,298 vs 358).

---

## 2. Overall Growth Summary

| Metric | D1 (Baseline) | D2 (Load) | D3 (Cooldown) |
|--------|--------------|-----------|---------------|
| **Total Objects** | 2,03,411 | **1,08,47,965** | 73,19,397 |
| **Heap — SOH** | 14.02 MB | 853.75 MB | 383.66 MB |
| **Heap — LOH** | 25.67 MB | 208.76 MB | 312.11 MB |
| **Heap — Total** | **39.69 MB** | **1.04 GB** | **695.77 MB** |
| **LOH Object Count** | 35 | 489 | 293 |
| **Finalize Queue** | 358 | **1,24,678** | **24,298** |
| **Unique Strings** | 9,501 | 86,195 | 66,165 |
| **Total String Memory** | 1.65 MB | 97.08 MB | 18.12 MB |
| **Event Instances** | 0 | **57,314** | **215** |
| **Event Types** | 0 | **186** | **11** |

### GC Heap Breakdown (DumpDetective)

| Segment | D1 | D2 | D3 |
|---------|----|----|----|
| SOH (`<` 85 KB objects) | 2,03,376 objects / 14.02 MB | 1,08,47,476 objects / 853.75 MB | 73,19,104 objects / 383.66 MB |
| LOH (`≥` 85 KB objects) | 35 / 25.67 MB | 489 / 208.76 MB | 293 / 312.11 MB |
| LOH — Free space | 35 / 25.67 MB | 306 / 121.47 MB | 61 / 197.78 MB |
| LOH — Int64[] | 0 | 104 / 53 MB | 184 / 95 MB |
| LOH — DataRow[] | 0 | 13 / 6.63 MB | 23 / 11.88 MB |
| LOH — Byte[] | 0 | 51 / 25.44 MB | 8 / 4.94 MB |
| LOH — Dictionary (AuthorizedEntityData) | 0 | 3 / 489 KB | 5 / 782 KB |

> **LOH growth from D2→D3:** Despite 33% fewer total objects, LOH grew from 208 MB to 312 MB. `Int64[]` arrays grew from 53 MB (104 arrays) to 95 MB (184 arrays). These are EF materialization buffers or large field-value arrays anchored by the 3 orphaned SessionStore/Principal objects, the static `InstallBaseData` cache, and the 9 never-closed `TdsParserStateObject` roots.

---

## 3. Root Cause Chain

```
D2: 57,314 live event subscriptions across 186 event types
  │
  ├─► [HIGH] Session + per-session objects
  │     133 Sessions under load → 0 Session objects at cooldown (fully collected)
  │     3 orphaned SessionStore subscriptions survive at D3 — stores not disposed after Session teardown
  │     Each active session anchors: Principal, EntityManager, authorization arrays, ObjectContext
  │
  ├─► [HIGH] ServiceDocumentsStore event subscriptions (load-time, fully recovered by D3)
  │     ServiceDocumentsStore._onSavingChanges: 121 instances (78 System.EventHandler + 43 Store)
  │     ServiceDocumentsStore.BeginDbTransaction, CommitDbTransaction, etc.: 78 each
  │     ServiceOrderDocument events: 90 each (PropertyChanged, VirtualEntityFieldChanged, etc.)
  │
  ├─► [HIGH] InstallBaseData — 1,388 permanent (D2 = D3, zero recovery)
  │     Anchored by Dictionary<EntityBase, ?> in LOH (3→5 copies, 489 KB → 782 KB)
  │
  ├─► [MEDIUM] Finalize Queue driven by DataColumn/DataTable not Disposed
  │     D2: 113,684 DataColumn + 2,153 DataTable queued = 99% of 124,678 finalize queue
  │     D3: 22,379 DataColumn + 248 DataTable queued = 93% of 24,298 finalize queue
  │     → DataTable/DataSet objects created inside EF queries, never explicitly Disposed
  │
  └─► [MEDIUM] 9 TdsParserStateObject rooted as StrongHandle at D3
        These are SqlConnection native state objects — connections that were never closed
        Incoming ref counts: 314,353 / 159,928 / 112,755 — each referenced by ~100K DataRows
        → One or more SqlConnections are held open indefinitely against a large query result
```

---

## 4. Event Leak Analysis (DumpDetective)

### D2 — 57,314 Instances Across 186 Event Types

DumpDetective identified **57,314 live event subscriptions** during peak load. The vast majority are ELSAPM async delegate containers — an internal FW5 pattern for async database operations that captures closures without releasing them when the operation completes.

#### Top 10 Event Types by Instance Count (D2):

| # | Event Type | Instances | Primary Subscriber |
|---|-----------|-----------|-------------------|
| 1 | `Excellon.FW5.ELSAPM.Extension+<>c__DisplayClass32_0.action` (**two generic variants**) | **31,958** | `<Object>`: `DataEntity+<>c__DisplayClass256_1` (31,723); `<DataSet>`: `DbStore+<>c__DisplayClass13_0` (127), `DbStore+<>c__DisplayClass14_0` (72) |
| 2 | `Excellon.FW5.ELSAPM.Extension+<>c__DisplayClass35_0.action` | **19,388** | `EntityBase+<>c__DisplayClass75_0` (6,290), `DataEntity+<>c__DisplayClass259_1` (5,894) |
| 3 | `Excellon.FW5.Principal.<RefreshAuthorizationData>` | **135** | `RefreshAuthorizationDataDelegate` (135 — one per authenticated `Principal`; matches ~133 sessions ± race) |
| 4 | `ServiceDocumentsStore._onSavingChanges` | **121** | `System.EventHandler` (78), `ServiceDocumentsStore` (43) |
| 5 | `MetadataStore._onSavingChanges` | **102** | `MetadataStore` (102) |
| 6 | `DefaultDataTemplateMaster.ManagerChanged` | **102** | `BALDefaultDataTemplateMaster` (102) |
| 7 | `ServiceManager.EntityStateChanged` | **96** | `ServiceOrderDocument` (44), `EntityStateChangeEventHandler` (23), `BillJobCardUtility` (18), `AppointmentDocument` (11) |
| 8 | `ServiceOrderDocument.PropertyChanged` | **90** | `PropertyChangedEventHandler` (90) |
| 9 | `ServiceOrderDocument.VirtualEntityFieldChanged` | **90** | `BALServiceOrderDocument` (67), `VirtualEntityFieldChangedEventHandler` (23) |

Additionally, 10 `TaxPostingStore._onSavingChanges` instances, 66 `TransactionDataStore._onSavingChanges`, 61 `SessionStore._onSavingChanges`, 11 `AppointmentDocument.*` event types (11 instances each), and 78 `ChargeBaseExtender` closure instances were identified across the remaining 176 event types.

#### Key Finding — ELSAPM Async Delegate Leak:
`DisplayClass32_0` and `DisplayClass35_0` are both compiler-generated classes for **lambda closures in async EF/ELSAPM operations**. `DisplayClass32_0` appears under two generic type parameters (`<Object>` and `<DataSet>`) — both are the same class and the same root cause, totalling **31,958 instances** combined. Together with `DisplayClass35_0` (19,388), the two classes account for **51,346 leaked event delegates**. These indicate that:
1. Async database operations capture entity references in closures
2. Those closures are registered as event delegates
3. The event sources are never unregistered after the async operation completes
4. Each DataEntity loaded into an active session creates permanent delegate allocations

This is likely the primary driver of the `1,80,621 ObjectPropertyMapping` objects and 26.86 million `Int64` values seen in D2's top-by-count table.

---

### D3 — 215 Instances Across 11 Event Types (Post-Cooldown)

By D3 (5h45m after load), **99.6% of all event subscriptions** from D2 had been released. Only 215 remain — all attributable to 3 root causes:

| # | Event Type | D3 Count | Root | Status |
|---|-----------|----------|------|--------|
| 1 | `DefaultDataTemplateMaster.ManagerChanged` | **102** | `BALDefaultDataTemplateMaster` (102) | ❌ Permanent leak — BAL layer never unsubscribes |
| 2 | `MetadataStore._onSavingChanges` | **37** | `MetadataStore` (37) | ❌ Permanent — stores survive session lifetime |
| 3 | `ELSAPM.Extension<DataSet>.action` | **30** | `DataEntityStore+DisplayClass40_0` (24), `DbStore+DisplayClass13_0` (6) | ⚠️ Residual closures |
| 4 | `SNINativeMethodWrapper+ConsumerInfo.readDelegate` | **15** | `SqlAsyncCallbackDelegate` | ⚠️ Open SQL reads |
| 5 | `SNINativeMethodWrapper+ConsumerInfo.writeDelegate` | **15** | `SqlAsyncCallbackDelegate` | ⚠️ Open SQL writes |
| 6 | `DefaultTemplateStore._onSavingChanges` | **5** | `DefaultTemplateStore` | ❌ Permanent |
| 7 | `ELSAPM.Extension DisplayClass35_0.action` | **3** | `DbStore+DisplayClass11_0` | ⚠️ Residual |
| 8 | `SessionStore._onSavingChanges` | **3** | `SessionStore` | ❌ Orphaned stores — parent `Session` objects were collected but `SessionStore.Dispose()` was not called |
| 9 | `ELSAPM.Extension<SessionCompactData>.action` | **3** | `DataEntityStore+DisplayClass61_0` | ❌ Anchored by the 3 orphaned SessionStore objects |
| 10 | `Principal.<RefreshAuthorizationData>` | **1** | `RefreshAuthorizationDataDelegate` | ❌ One orphaned `Principal` — parent `Session` collected but `Principal.Dispose()` not called |
| 11 | `LocalizeStore._onSavingChanges` | **1** | `LocalizeStore` | ❌ Permanent |

---

## 5. Finalize Queue Detail

DumpDetective provides the most diagnostic breakdown of finalize queue composition:

| Type in Finalizer Queue | D1 | D2 | D3 |
|------------------------|----|----|-----|
| `System.Data.DataColumn` | 0 | **113,684** | **22,379** |
| `System.Data.DataTable` | 0 | 2,153 | 248 |
| `System.Data.DataSet` | 0 | 704 | 0 |
| `System.Threading.ReaderWriterLock` | 0 | 2,158 | 253 |
| `System.Data.SqlClient.SqlCommand` | 0 | 1,049 | 92 |
| `System.Data.SqlClient.SqlConnection` | 0 | 619 | 67 |
| `System.Data.EntityClient.EntityConnection` | 0 | 601 | 0 |
| `System.Reflection.Emit.DynamicResolver` | 0 | 484 | 266 |
| `System.Web.HttpResponseUnmanagedBufferElement` | 0 | 381 | 0 |
| `System.WeakReference` | 39 | 799 | 229 |
| `System.Threading.Thread` | 9 | — | 96 |
| `System.Data.SqlClient.SqlConnection` (again, D3) | 0 | 619 | 67 |
| `ThreadLocal<ConcurrentBag<LogEntryData>>+FinalizationHelper` | 0 | — | **100** |
| **Total** | **358** | **1,24,678** | **24,298** |

### Critical Observation — DataColumn as Finalize Blocker
`System.Data.DataColumn` queued 113,684 objects for finalization in D2 (representing 91% of the entire finalize queue). This happens because:
1. `DataTable` objects holding these columns are never explicitly `.Dispose()`'d
2. When the GC finds them unreachable, they enter the finalizer queue
3. The finalizer must run to release unmanaged column metadata handles
4. With 113K queued columns under load, the finalizer thread is severely backlogged

At D3, 22,379 DataColumns remain in queue — the finalizer has processed ~80% but the queue will not reach zero without fixing the DataTable disposal path.

---

## 6. Highly Referenced Objects (Potential Leak Roots)

DumpDetective's reference analysis surfaces the single most important leak anchor in the application:

### D2 — Peak Load
| Object | Address | Incoming References | Significance |
|--------|---------|-------------------|-------------|
| `System.String` (db connection string) | `0x1FD0F2B1420` | **378,146** | Connection string referenced by every SqlConnection/Command across all sessions |
| `System.Data.SqlClient.TdsParserStateObject` | `0x1FE0F4983E0` | **325,560** | One TDS parser state shared by ~325K objects — likely the primary open connection |
| `System.DBNull` | `0x1FD8F2DC3D0` | **218,308** | DBNull.Value shared reference — normal, but scale indicates massive DataRow volume |
| `System.Data.DataTable` (×4 instances) | various | **104,803 each** | 4 DataTables with identical ~105K reference count — static/cached tables loaded once |
| `System.Data.DataColumnCollection` (×4) | various | **104,778 each** | Column collections of the above DataTables |

### D3 — Cooldown
| Object | Address | Incoming References | Significance |
|--------|---------|-------------------|-------------|
| `TdsParserStateObject` | `0x1FF1195B430` | **314,353** | **Grows D2→D3** — an open SqlConnection accumulated more references during cooldown activity |
| `TdsParserStateObject` | `0x1FF90886F88` | **159,928** | Second open connection with large DataRow fan-out |
| `TdsParserStateObject` | `0x1FF9086BB60` | **112,755** | Third open connection |
| `System.Data.DataTable` (×7 instances) | various | **104,803 each** | 7 cached DataTables at D3 vs 4 at D2 — cache grew during cooldown |

> **The 314K-reference TdsParserStateObject** is the most critical finding from D3. A single SqlConnection is being referenced by 314,353 objects — this means ~314K DataRows, DataColumns, or query result objects are all keeping a reference back to the same TDS state. This connection was never closed and its result set was never released. This is the LOH anchor explaining why LOH **grew** from D2 to D3.

---

## 7. Rooted Objects Analysis

DumpDetective confirms which objects are held by strong GC roots and cannot be collected:

| Root Type | D1 | D2 | D3 |
|-----------|----|----|-----|
| `System.Object[]` (StrongHandle) | 37 / 300 KB | 478 / 1.13 MB | 147 / 1.11 MB |
| `System.Data.SqlClient.TdsParserStateObject` (StrongHandle) | 0 | **107 / 51 KB** | **9 / 4.29 KB** |
| `System.Threading.Thread` (StrongHandle) | 9 / 864 B | 68 / 6.38 KB | 30 / 2.81 KB |
| `System.Threading.Timer` (StrongHandle) | 18 | 18 | 18 |
| `System.Web.Caching.CacheSingle` | 16 | 16 | 16 |
| `System.Runtime.Remoting.ServerIdentity` | 11 | 10 | 10 |
| `System.Web.NativeFileChangeNotification` | 6 | 8 | 8 |

Key observations:
- **18 `System.Threading.Timer` instances are rooted in all 3 dumps** — these were created at startup and never disposed. Their callbacks will fire indefinitely.
- **107 `TdsParserStateObject` rooted as StrongHandle at D2** dropped to 9 at D3 — 98 connections were properly closed during the cooldown, but 9 remain permanently open.
- **D3 has 30 rooted Threads vs 9 at D1** — 21 additional threads were created under load and are still tracked despite being dead (not alive per thread analysis).

---

## 8. Duplicate String Analysis

DumpDetective's string analysis reveals **business data strings** being allocated redundantly rather than interned or cached:

### D2 Most Duplicated Strings (Top 5):

| String | Count | Wasted Memory | Root Cause |
|--------|-------|---------------|-----------|
| `"Service Manager 3W"` | **64,896** | **3.84 MB** | Role/permission name loaded per-entity, not interned |
| `"URB Account Manager"` | **20,412** | **1.25 MB** | Same pattern — another role name |
| `http://schemas.microsoft.com/...` | **10,198** | **1.24 MB** | WCF/XML serialization namespace — expected duplication |
| `"32869f17-2771-4ff5-8bd4-16831bd18f66"` (GUID) | **9,927** | **949 KB** | Same entity GUID referenced across 10K+ objects |
| `"Service_Job Card_Add"` | **11,655** | **751 KB** | Permission name — loaded fresh per session |

### D3 Most Duplicated Strings (Top 5):

| String | Count | Wasted Memory | Root Cause |
|--------|-------|---------------|-----------|
| `http://schemas.microsoft.com/...` | **10,198** | **1.24 MB** | Permanent — WCF schema constant |
| `"32869f17-2771-4ff5-8bd4-16831bd18f66"` | **9,002** | **861 KB** | Entity GUID — partially recovered |
| `"False"` | **4,295** | **150 KB** | Boolean string representation — not interned |
| `"BUS-4105"` | **3,521** | **144 KB** | Business code/document number — cached data |
| `"Service3W_03"` | **1,745** | **85 KB** | Service category code |

> The `"Service Manager 3W"` duplication from 64,896 copies (D2) dropping sharply at D3 confirms most session-scoped permission string allocations are freed when sessions are disposed. The residual copies at D3 are anchored by the 3 orphaned `SessionStore`/`Principal` objects and the static `InstallBaseData` cache.

---

## 9. Fix Recommendations (Priority Order)

### P0 — Fix ELSAPM Async Delegate Leak — **51,346 live delegates under load** (CRITICAL)

`DisplayClass32_0` (31,958 total — `<Object>` variant: 31,726; `<DataSet>` variant: 232) and `DisplayClass35_0` (19,388) are compiler-generated closures created for every async EF/ELSAPM database call. They are the same root cause — two code paths in `ELSAPM.Extension` that each generate their own `DisplayClass` but share the identical leak pattern. Neither is released after the call completes.

**Pattern:** The ELSAPM extension method registers a continuation lambda as an event delegate. When the async operation finishes, the delegate is never unregistered (`-=`) and the containing `DisplayClass` object is kept alive by the event source.

**Required fix:**
```csharp
// Wrong — delegate captured and never released:
entity.SomeEvent += async (s, e) => { await _store.LoadAsync(entity); };

// Correct — store reference so it can be unsubscribed:
DataEntity.AsyncOperationCompleted += _completionHandler;

// In Dispose():
DataEntity.AsyncOperationCompleted -= _completionHandler;
```
Or use `IDisposable`-scoped subscriptions / `CancellationToken` patterns to automatically remove lambda delegates when the owning operation scope exits.

---

### P1 — Fix Orphaned Per-Session Store Disposal (3 orphaned stores at D3) — HIGH

All 133 `Session` objects from D2 are fully collected by D3 — session *object* lifecycle is correctly handled. However, 3 `SessionStore` and 1 `Principal` instances survive at D3 as orphaned objects, meaning `Session.Dispose()` did not propagate to properly call `SessionStore.Dispose()` and `Principal.Dispose()` on every code path.

The 61 `SessionStore._onSavingChanges` instances at D2 vs 3 at D3 also tells the same story: most session teardowns are clean, but a narrow code path (likely WCF fault channels or timeout-expired requests) completes `SessionManager.Remove()` without fully disposing the session's child stores.

```csharp
// Safety-net: periodically sweep sessions with no LastActivity in N minutes
_sweepTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds);
_sweepTimer.Elapsed += (_, _) => SweepExpiredSessions();
_sweepTimer.Start();

private void SweepExpiredSessions()
{
    var expiredIds = _sessions
        .Where(s => s.Value.LastActivity < DateTime.UtcNow.AddMinutes(-30))
        .Select(s => s.Key).ToList();
    foreach (var id in expiredIds)
        if (_sessions.TryRemove(id, out var s)) s.Dispose();
}
```

---

### P2 — Dispose DataTable/DataSet Explicitly (113,684 DataColumns in finalize queue) — HIGH

`System.Data.DataColumn` accounts for 91% of the peak finalize queue (113,684/124,678). Every `DataTable` created for query results that is not explicitly `.Dispose()`'d will queue all its columns for finalization. With 765,977 DataRows at D2 spanning many tables, the finalizer thread is completely saturated.

```csharp
// Always wrap DataSet/DataTable in using:
using (var ds = new DataSet())
using (var da = new SqlDataAdapter(cmd))
{
    da.Fill(ds);
    // process...
} // Dispose() called — columns are freed immediately, not queued
```
Also: any EF query that materializes via `DataSet` internally must go through `ObjectContext.ExecuteStoreQuery<T>()` with a `using` scope, or use typed `IQueryable<T>` queries that don't create intermediate DataTables.

---

### P3 — Fix the Never-Closed SqlConnections (9 rooted `TdsParserStateObject`) — HIGH

At D3, 9 `TdsParserStateObject` instances remain rooted as `StrongHandle` GC roots. The largest has 314,353 incoming references — meaning ~314K objects (DataRows, columns, field values) are anchored to one open, never-returned-to-pool connection.

```csharp
// Ensure ALL connections use using blocks:
using (var conn = new SqlConnection(_connectionString))
using (var cmd = new SqlCommand(sql, conn))
{
    conn.Open();
    using (var reader = cmd.ExecuteReader())
    {
        // ...
    }
} // conn.Close() + return to pool guaranteed
```
Also audit any `SqlDataAdapter.Fill()` usage where the `SqlConnection` is passed in — verify that the calling code owns and disposes the connection correctly.

---

### P4 — Fix InstallBaseData Static Cache (1,388 instances, zero recovery) — HIGH

The 5 `Dictionary<Excellon.FW5.Base.EntityBase, ?>` objects in the LOH at D3 (total 782 KB) are the direct container holding the 1,388 `InstallBaseData` entries. This is a process-lifetime static dictionary that is never cleared.

**Required fix:** Replace with a bounded, evicting cache:
```csharp
// Replace:
private static readonly Dictionary<int, InstallBaseData> _cache = new();

// With:
private static readonly MemoryCache _cache = new MemoryCache(
    new MemoryCacheOptions { SizeLimit = 500 });

public InstallBaseData Get(int id) =>
    _cache.GetOrCreate(id, e => { e.Size = 1; e.SlidingExpiration = TimeSpan.FromMinutes(30); return LoadFromDb(id); });
```

---

### P5 — Intern or Pool Repeated Business Strings — MEDIUM

`"Service Manager 3W"` was allocated 64,896 times in D2, wasting 3.84 MB. At 200+ concurrent users this will be worse. Use `string.Intern()` for known constant role/permission names, or load them once into a static lookup:

```csharp
// Instead of returning new string from every authorization query:
return row["RoleName"].ToString();

// Use interned or pre-allocated values:
private static readonly string[] _knownRoles = ["Service Manager 3W", "URB Account Manager", ...];
return string.Intern(row["RoleName"].ToString());
```

---

### P6 — Unsubscribe `DefaultDataTemplateMaster.ManagerChanged` (102 permanent) — MEDIUM

102 `BALDefaultDataTemplateMaster` objects remain subscribed at D3 (in both D2 and D3 — unchanged). This event fires whenever default data templates change and the BAL wraps the entity in a lifecycle subscription that is never removed.

```csharp
// BALDefaultDataTemplateMaster.Dispose():
protected override void Dispose(bool disposing)
{
    if (disposing)
        _template.ManagerChanged -= OnManagerChanged;
    base.Dispose(disposing);
}
```

---

### P7 — Stop/Dispose 18 Leaked Timers — MEDIUM

18 `System.Threading.Timer` instances are rooted as `StrongHandle` in all 3 dumps (identical count — created at startup, never stopped). Timer callbacks keep their target objects alive indefinitely.

```csharp
// In application teardown / AppDomain unload:
Application_End(object sender, EventArgs e)
{
    _backgroundTimer?.Dispose();
    _cacheRefreshTimer?.Dispose();
    // all 18 must be accounted for
}
```

---

## 10. Analysis Artifacts

| File | Tool | Description |
|------|------|-------------|
| `dd_D1_11_44AM.txt` | DumpDetective.exe | Full analysis of D1 — baseline (714 MB dump) |
| `dd_D2_12_58PM.txt` | DumpDetective.exe | Full analysis of D2 — load peak (3.33 GB dump) |
| `dd_D3_06_43PM.txt` | DumpDetective.exe | Full analysis of D3 — cooldown (2.70 GB dump) |
| `analysis_D1_11_44AM.txt` | dotnet-dump | Supplementary type counts — D1 |
| `analysis_D2_12_58PM.txt` | dotnet-dump | Supplementary type counts — D2 |
| `analysis_D3_06_43PM.txt` | dotnet-dump | Supplementary type counts — D3 |
| `w3wp.exe..._06_43_20PM..._Manual Dump.txt` | Custom event scanner | Event leak inventory for D3 |
