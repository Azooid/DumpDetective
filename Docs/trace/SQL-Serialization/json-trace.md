# json-trace

**Category:** Trace — SQL & Serialization  
**Included in `trace-analyze`:** Yes

## What it does

Measures the CPU time and allocation pressure caused by JSON serialization and deserialization in a `.nettrace` or `.etl` trace. Combines two independent ETW signals:

- **`GCAllocationTick`** — fired every ~100 KB of allocations; filters types from `System.Text.Json.*`, `Newtonsoft.Json.*`, and `System.Runtime.Serialization.Json.*` to measure JSON-related heap pressure.
- **`SampledProfile` / `PerfInfo/Sample`** — CPU stack samples; filters frames whose method name contains `System.Text.Json`, `Newtonsoft.Json`, or `JsonSerializer` to measure CPU time spent inside serialization code paths.

For both signals, the **caller frame** — the first user-code frame directly above the JSON library frame in the call stack — is recorded to show *which of your code* triggers JSON work most frequently.

---

## Collecting a trace

### dotnet-trace — allocation only

```bash
dotnet trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' \
  -p <pid>
```

### dotnet-trace — CPU + allocation (recommended)

```bash
dotnet trace collect --profile cpu-sampling \
  --providers 'Microsoft-Windows-DotNETRuntime:0x1:5' \
  -p <pid>
```

### PerfView

```bash
PerfView.exe /ClrEvents:GC,Type,GCHeapAndTypeNames,Default /KernelEvents:Profile /NoGui collect
```

---

## Options

| Option | Description |
|---|---|
| `<trace>` | Path to `.nettrace` or `.etl` file |
| `--top <N>` | Top N types / callers to show (default: 20) |
| `--process <name>` | Filter to a specific process name |
| `-o, --output <file>` | Output path (`.html`, `.md`, `.txt`, `.json`) |

---

## Report sections

| Section | Content |
|---|---|
| **Summary** | Total JSON CPU samples, total JSON allocation bytes, unique caller frames |
| **Top Allocation Types** | JSON types ranked by estimated bytes allocated |
| **Top Callers** | User-code frames: CPU samples, alloc bytes, combined signal |
| **CPU samples chart** | Stacked bar — top 6 callers by CPU samples |
| **Allocation chart** | Stacked bar — top 6 callers by estimated allocation bytes |

---

## What to look for

| Signal | What it means |
|---|---|
| **High CPU sample % in JSON frames** | Serialization is a CPU bottleneck — consider `[GeneratedJsonSerializer]` source-gen or `Utf8JsonWriter` for hot paths |
| **Large allocation bytes from `JsonDocument`** | `JsonDocument` is rented from a pool but backing arrays can be large — prefer `Utf8JsonReader` for read-only parsing |
| **Newtonsoft.Json frames with high samples** | Consider migrating to `System.Text.Json` which is ~2–4× faster and allocation-friendly |
| **Same caller frame dominating both CPU and alloc** | That endpoint/method is doing all the JSON work — prime candidate for caching serialized output |
| **Many small callers** | JSON cost is diffuse across many paths — profile at request level with `http-trace` to find the hot endpoint |
| **High alloc with low CPU samples** | Serialization is fast but generates excessive garbage — check for `string` intermediaries instead of `ReadOnlySpan<byte>` |
