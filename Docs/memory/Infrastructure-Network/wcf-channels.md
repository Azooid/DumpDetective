# wcf-channels

**Category:** Infrastructure / Network  
**Included in `analyze --full`:** Yes

## What it does

Finds all live WCF (`System.ServiceModel.*`) channel and channel-factory objects on the heap. Shows communication state, endpoint address, binding type, and fault reason when faulted. Identifies stuck, faulted, or leaked WCF connections.

The report distinguishes **client proxy channels** (objects with a meaningful `CommunicationState` and endpoint address) from **server-side WCF hosting infrastructure** (`ServiceHostBase`, `OperationContext`, `ServiceHttpModule`, config sections, etc.). When only server-side objects are present, the report explains this rather than showing "No endpoint addresses resolved".

---

## Analyzer: `WcfChannelsAnalyzer`

**Implements:** `IHeapObjectConsumer`  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

`HeapTypeMeta.IsWcf` is set when the CLR type name starts with `System.ServiceModel.`.

### How it works

For each WCF object, four fields are read:

**State** is read by trying `_state`, `_communicationState`, `state` in order, mapping the integer to: 0=`Created`, 1=`Opening`, 2=`Opened`, 3=`Closing`, 4=`Closed`, 5=`Faulted`.

**Endpoint address** is resolved by trying `_remoteAddress`, `_via`, `_listenUri`, `_address` in order. Each candidate is read as an object, then its `._string` direct field is tried; if that fails, the nested `._uri._string` path is tried. The first non-empty result is used.

**Binding type** is the type name of the object in the `_binding` or `Binding` field (e.g. `NetTcpBinding`, `BasicHttpBinding`).

**Fault reason** is only read when state is `Faulted`. Field names `_faultReason`, `faultReason`, `_closedException` are tried in order. String fields are read directly; exception object fields have their `.Message` field read.

All field reads are wrapped in try/catch. Clone/merge: each clone collects its own object list; `MergeFrom` appends them.

---

## Pre-warm path

`WcfChannelsAnalyzer` itself is the consumer in `DumpCollector.CollectHeapObjectsCombined` — the full detail analyzer (state, endpoint, binding, fault reason reads) runs directly during the combined walk. Populates `WcfChannelsData` in the session cache.

---

## Consumer: `LightweightStatsConsumer` (count only)

**Class**: `DumpDetective.Analysis.Memory.Consumers.LightweightStatsConsumer`  
**Walk**: `HeapObjectCollector.CollectHeapObjects` — lightweight scan, no full context  
**Purpose**: Increments `WcfCount` for all WCF objects and `WcfFaulted` for those with state integer 5 (reading `_state`, `_communicationState`, or `state` in priority order). Feeds `DumpSnapshot.WcfCount` and `WcfFaultedCount` for health scoring. No endpoint or binding data.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--state <name>` | Filter by state (e.g. `Faulted`, `Opened`, `Closing`) |
| `--filter <text>` | Substring match on endpoint address or binding type |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Many `Faulted` channels** | Channels hit a communication error and were never `Abort()`ed and recreated. After a fault, WCF channels are permanently broken — they hold resources and cannot be reused. |
| **Many `Opened` channels** | Channels held open indefinitely — check if channel factories use `ChannelFactory<T>` caching correctly and if channels are returned/closed after use. |
| **Many `Closing` channels** | Close handshake hanging — network timeout or server not responding to the two-phase WCF close. Use `Abort()` when `Close()` hangs. |
| **`_faultReason` reveals specific error** | `"The remote side closed the connection"` = server restart. `"The socket connection was aborted"` = network issue. `"Quota exceeded"` = message size or timeout limit hit. |
| **High count from a single endpoint** | One downstream service accumulated stuck connections — timeout/retry storm. |
| **`NetTcpBinding` channels in bulk** | TCP-based WCF (internal services). Each faulted channel holds a TCP socket until it's aborted. |

---

## Typical output shape

```
WCF Channels — 1,247 objects found

State     Endpoint                              Binding           Fault Reason             Count
Faulted   net.tcp://payments-svc:8080/Svc       NetTcpBinding     Remote side closed         983
Opened    net.tcp://inventory-svc:8080/Svc      NetTcpBinding     —                          218
Closing   https://auth.internal/AuthSvc         BasicHttpBinding  —                           46
```
