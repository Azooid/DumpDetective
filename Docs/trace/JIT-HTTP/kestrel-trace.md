# kestrel-trace

**Category:** Trace — HTTP and ASP.NET  
**Included in `trace-analyze`:** Yes

## What it does

Analyzes `Microsoft.AspNetCore.Server.Kestrel` events to detect connection rejections, request queue pressure, and per-connection error rates. Surfaces Kestrel-level issues that are invisible at the HTTP layer: dropped connections, backpressure activation, and TLS handshake failures.

---

## Analyzer: `KestrelTraceAnalyzer`

**Namespace:** `DumpDetective.Analysis.Trace.Analyzers`

Parses Kestrel event names: `ConnectionStart`, `ConnectionStop`, `ConnectionReset`, `RequestBodyStart`, `RequestBodyDone`, `ResponseBodyStart`, `ResponseBodyDone`, and `ApplicationError`. Connection reset events are classified by `errorCode` (RST_STREAM, GOAWAY, etc.). Backpressure activation is detected from `RequestBodyPaused` events. Per-connection error rates are computed and the top error connections are reported.

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
| High `ConnectionReset` rate | Client-side disconnects or TLS negotiation failures |
| Backpressure events | Response buffer full; slow clients or large responses |
| `ApplicationError` on connections | Unhandled exceptions in middleware or controllers |
| Connection count spike with no request count spike | Keep-alive connections accumulating (check client timeout settings) |
