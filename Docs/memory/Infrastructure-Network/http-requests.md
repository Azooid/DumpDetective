# http-requests

**Category:** Infrastructure / Network  
**Included in `analyze --full`:** Yes

## What it does

Finds all live `System.Net.Http.*` and `System.Net.HttpWebRequest` objects on the heap. Shows HTTP method, URI, and status code where readable. Identifies abandoned in-flight requests, misused `HttpClient` instances, and leaking response objects.

---

## Analyzer: `HttpRequestsAnalyzer`

**Class type:** Uses `HttpRequestsConsumer` for pre-warm; standalone walk when not pre-warmed  
**Namespace:** `DumpDetective.Analysis.Memory.Analyzers`

### Types matched

Six type names are recognized: `System.Net.Http.HttpRequestMessage`, `System.Net.Http.HttpResponseMessage`, `System.Net.HttpWebRequest`, `System.Net.Http.HttpClient`, `System.Net.Http.HttpClientHandler`, and `System.Net.Http.SocketsHttpHandler`. The `HeapTypeMeta.IsHttp` flag is set per MethodTable for all six.

---

## Consumer: `HttpRequestsConsumer`

**Class**: `DumpDetective.Analysis.Memory.Consumers.HttpRequestsConsumer`  
**Implements**: `IHeapObjectConsumer`  
**Walk**: `DumpCollector.CollectHeapObjectsCombined`  
**Output**: `HttpRequestsData`, cached via `ctx.SetAnalysis<HttpRequestsData>()`

### How it works

For each object where `meta.IsHttp` is true, the consumer records type, address, and size. For `HttpRequestMessage`, it reads the HTTP verb by navigating `_method → HttpMethod._method (string)`, and the URI by navigating `_requestUri → System.Uri._string`. For `HttpResponseMessage`, it reads `_statusCode` as an int. All other HTTP types are recorded with empty method/URI and status 0.

All field reads are wrapped in try/catch because field names and nesting changed between .NET Framework, .NET 5+, and .NET 6+. Clone/merge: each clone collects its own entry list; `MergeFrom` appends them.

---

## Pre-warm path

`HttpRequestsConsumer` runs during `DumpCollector.CollectHeapObjectsCombined`. Populates `HttpRequestsData` in the session cache.

---

## Disk cache

None.

---

## Options

| Option | Description |
|---|---|
| `--filter <text>` | Substring match on URI or type name |
| `--method <verb>` | Filter by HTTP method (e.g. `GET`, `POST`) |

---

## What to look for in the report

| Signal | What it means |
|---|---|
| **Large count of `HttpRequestMessage`** | Requests created but never sent or awaited — fire-and-forget anti-pattern, or requests held in a collection without being dispatched |
| **Many `HttpClient` instances** | `HttpClient` must be shared/pooled — creating one per request exhausts socket descriptors (`TIME_WAIT` port exhaustion). Use `IHttpClientFactory` or a static shared instance. |
| **Many `HttpClientHandler` / `SocketsHttpHandler`** | Each handler holds its own connection pool. Many handlers = many separate pools = port exhaustion independently of `HttpClient` count. |
| **`HttpResponseMessage` objects present** | Responses not disposed — holds response body `HttpContent` buffer alive. Call `response.Dispose()` or wrap in `using`. |
| **Specific URI appearing repeatedly** | All requests targeting one endpoint are in-flight — that endpoint is slow, rate-limiting, or unreachable. |
| **URIs containing private IPs or unexpected hosts** | Misconfiguration — requests going to wrong environment. |
| **`HttpWebRequest` objects** | Legacy API in use — consider migration to `HttpClient` for better connection pool management. |

---

## Typical output shape

```
HTTP Requests — 3,841 objects found

Type                          Method   URI / Info                          Status   Count   Total Size
HttpRequestMessage            POST     https://api.payments.internal/v2    —        3,124    48.2 MB
HttpClient                    —        —                                   —          712    22.8 MB
HttpResponseMessage           —        —                                   200          5   180 KB
HttpClientHandler             —        —                                   —            0   <1 KB
```
