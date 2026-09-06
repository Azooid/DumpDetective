# 🔍 DumpDetective

**Your production service just fell over. You have a .dmp file and no idea where to start.**

DumpDetective turns that dump into an **interactive HTML report in one command** — health score, prioritized findings, and **30 targeted sub-reports** covering memory leaks, thread deadlocks, async backlogs, GC pressure, event handler leaks, and more. All run in parallel from a single heap walk.

Works with **.nettrace** and **.etl** trace files too: **29 trace sub-reports** covering CPU hot paths, allocations, GC pauses, contention, HTTP, SQL, ThreadPool starvation, and more.

Also analyzes **Chrome DevTools performance traces** (`.json`/`.json.gz`) — browser JS/DOM memory leaks, CPU hotspots, long tasks, GC pressure, jank, input latency, and network, with a `.NET`-stack-trace-style **Call Stack** on every hotspot so a shared vendor function is always traced back to the application code that called it (or the closest timing-correlated signal when it can't be, e.g. across a promise/deferred boundary).

**No steep learning curve. No manual .windbg commands. No hours debugging. Just answers.**

---

## ⚡ Quick Start

**Step 1: Install (one time)**
```bash
dotnet tool install --global DumpDetective.Cli
```

**Step 2: Analyze (5–30 seconds)**
```bash
DumpDetective analyze app.dmp --full
# Creates app.html with health score, findings, and 30 sub-reports
```

**Step 3: Investigate**
- Open `app.html` in your browser
- Review the health score (0–100) and findings
- Explore interactive charts, sortable tables, and detailed call stacks
- Dark mode toggle available

**Maintenance**
```bash
dotnet tool update --global DumpDetective.Cli    # update to latest
dotnet tool uninstall --global DumpDetective.Cli # remove
```

---

## 🎯 Five Minutes to an Answer

```bash
# 1. Full scored report — 30 sub-reports in parallel
DumpDetective analyze app.dmp --full

# 2. Save for replay (no need to keep the dump file open later)
DumpDetective analyze app.dmp --full --output report.bin
DumpDetective render report.bin --output report.html

# 3. Pre-build caches once so every future run completes in seconds
DumpDetective load app.dmp

# 4. Trace file instead of a dump
DumpDetective trace-analyze app.nettrace

# 5. Both — cross-source correlation links trace signals to heap evidence
DumpDetective trace-dump-analyze app.nettrace app.dmp --output incident.html

# 6. Chrome DevTools performance trace (browser JS/DOM/CPU/network) instead of a .NET dump
DumpDetective web-analyze trace.json.gz --output report.html
```

The HTML report is self-contained: sticky sidebar navigation, collapsible sections, sortable/filterable tables, embedded charts, and a dark mode toggle. Share it as a single file.

---

## 🏆 What You Get

| Feature | Details |
|---|---|
| **Health score** | 0–100 score per dump, deducting points for each finding. Critical / Warning / Info grouped findings with actionable advice. |
| **30 memory sub-reports** | Run in parallel after one heap walk: leaks, dominator tree (exact retained memory via Lengauer-Tarjan), fragmentation, deadlocks, async backlogs, event handler leaks, static roots, and more. |
| **29 trace sub-reports** | One pass over the trace: CPU, allocations, GC, contention, exceptions, starvation, async tasks, JIT, HTTP, SQL, network I/O, OpenTelemetry, and more. |
| **Cross-source correlation** | trace-dump-analyze runs trace + dump together and applies 10 built-in correlation rules to surface the highest-confidence root causes. |
| **Web performance analysis** | 8 web-* commands analyze Chrome DevTools performance traces — JS heap/DOM/listener leaks, CPU hotspots, long tasks, GC pressure, jank, input latency, network. Every hotspot gets a `.NET`-stack-trace-style Call Stack traced back to your code (or a timing correlation when a call-tree edge can't reach it). |
| **Trend analysis** | trend-analysis compares multiple dumps over time — heap growth, type counts, event leaks, GC metrics. |
| **Report replay and diff** | Save any report as .bin (Brotli-compressed). Re-render to any format later. Diff two saved reports without reopening dumps. |
| **Plugin system** | Drop a .NET class library into plugins/ to add custom commands. They appear in --help and can run in analyze --full. |
| **Retained-size index** | .bfs.idx is built once per dump and reused on every run for instant BFS retained-size queries. |
| **Dominator index** | .idom.idx (Lengauer-Tarjan) is built automatically on first analyze --full and cached. Subsequent runs load it in ~2 s. |

---

## 💾 Memory Dump Commands

All commands work with `.dmp`, `.mdmp` (WinDbg), and `.core` (Linux) files. Commands run in seconds on cached dumps.

| Category | Commands |
|---|---|
| Orchestration | `analyze`, `trend-analysis` |
| Replay / diff | `render`, `diff` |
| Cache lifecycle | `load`, `close` |
| Heap overview | `heap-stats`, `gen-summary`, `memory-pressure`, `heap-fragmentation`, `large-objects`, `pinned-objects` |
| Memory leaks | `memory-leak`, `high-refs`, `string-duplicates`, `dominator-tree` |
| Retention signals | `cache-patterns`, `closure-capture`, `datatable-amp`, `static-refs`, `weak-refs`, `event-analysis`, `gc-root-map` |
| GC / lifetime | `finalizer-queue`, `handle-table` |
| Threads | `thread-analysis`, `thread-pool`, `deadlock-detection`, `async-stacks`, `native-interop` |
| Infrastructure | `connection-pool`, `http-requests`, `timer-leaks`, `wcf-channels`, `module-list` |
| Targeted | `type-instances`, `object-inspect`, `gc-roots` |

## ⏱️ Trace Commands

All commands work with `.nettrace` and `.etl` trace files. Analyze one trace or correlate multiple traces with a heap dump.

| Category | Commands |
|---|---|
| Combined | `trace-analyze`, `trace-dump-analyze` |
| CPU / alloc | `cpu-trace`, `alloc-trace`, `alloc-burst-trace` |
| GC / memory | `gc-trace`, `finalizer-trace`, `loh-trace` |
| Exceptions / locks | `contention-trace`, `exceptions-trace`, `deadlock-trace` |
| Threads / async | `threadpool-starvation`, `async-trace`, `context-switch-trace`, `task-scheduler-trace` |
| JIT | `jit-trace` |
| HTTP / ASP.NET | `http-trace`, `kestrel-trace`, `aspnetcore-pipeline-trace` |
| SQL / data | `sql-trace`, `json-trace`, `connection-pool-trace` |
| Network / I/O | `socket-trace`, `dns-trace`, `file-io-trace`, `handle-leak-trace` |
| Observability | `otel-trace`, `process-lifecycle-trace`, `retry-storm-trace` |
| Intelligence | `anomaly-trace`, `root-cause-trace` |

## 🌐 Web Performance Commands

Analyzes Chrome DevTools performance traces (`.json` / `.json.gz` — the "Enhanced Trace" export from the DevTools Performance panel): browser JS/DOM performance, not the .NET runtime.

| Category | Commands |
|---|---|
| Orchestration | `web-analyze` |
| Memory | `web-memory-leak` |
| CPU | `web-cpu-hotspots`, `web-long-tasks` |
| GC / rendering | `web-gc-pressure`, `web-jank` |
| Responsiveness | `web-input-latency` |
| Network | `web-network` |

See [Docs/documentation.md → Web Performance Commands](Docs/documentation.md#web-performance-commands) for full details, including how the Call Stack column and long-task blocker attribution work.

---

## 📄 Output Formats

Every command writes <dump-name>.html by default. Use -o / --output or --format to change it. Both flags are repeatable:

```bash
DumpDetective heap-stats app.dmp -o report.html -o report.bin  # both at once
DumpDetective analyze app.dmp --full --format bin               # auto-named .bin
```

| Extension | Description |
|---|---|
| .html | Interactive — sticky nav, charts, sortable tables, dark mode, fully self-contained |
| .md | Markdown |
| `.json` | Structured JSON, re-renderable via `render` |
| .bin | Brotli-compressed JSON (~50–70% smaller) |
| .txt | Plain text |

---

## 🔌 Plugins & Custom Commands

Extend DumpDetective with your own analysis logic — no source code changes needed.

Drop a .NET class library anywhere in plugins/ (next to the exe) or ~/.dumpdetective/plugins/ and DumpDetective loads it automatically. Your commands appear in --help, run standalone, and can participate in analyze --full.

```csharp
public sealed class TopStringsCommand : ICommand
{
    public string Name    => "top-strings";
    public string Description => "Top 20 strings by instance count.";
    public bool   IncludeInFullAnalyze => true;
    public string Category => "My Custom Commands";

    public int Run(string[] args)
    {
        var a = CliArgs.Parse(args);
        return CommandBase.Execute(a, Render);
    }

    public void Render(DumpContext ctx, IRenderSink sink)
    {
        CommandBase.RenderHeader("Top Strings", ctx, sink);
        var top = ctx.Heap.EnumerateObjects()
            .Where(o => o.Type?.Name == "System.String")
            .OrderByDescending(o => o.Size)
            .Take(20);
        sink.Table(["Address", "Size", "Value"],
            top.Select(o => new[] { o.Address.ToString("x"), o.Size.ToString(),
                                    o.AsString() ?? "" }).ToList());
    }
}
```

```bash
# Compile as a class library targeting net8.0 (or net10.0), then:
cp MyPlugin.dll plugins/
DumpDetective top-strings app.dmp
DumpDetective analyze app.dmp --full --with-plugins  # include in full-analyze
```

See [Docs/Plugins.md](Docs/Plugins.md) for the full API: heap-walk contribution, cache pinning, trace sub-analyzers, and cross-source correlation rules.

---

## 🛠️ Requirements

| Requirement | Details |
|---|---|
| **.NET runtime** | .NET 8+ (for `dotnet tool install`); .NET 10 SDK to build from source |
| **OS** | Windows for WinDbg `.dmp`/`.mdmp` dumps; Linux `.core` dumps supported since v3.3.0 |
| **RAM** | 4 GB+ for small dumps; 16–20 GB recommended for production dumps ≥15 GB |
| **Storage** | SSD strongly recommended — dumps are memory-mapped with random I/O patterns |
| **Performance** | First run builds caches (~5–30s); subsequent runs use cached indexes (typically <1s) |

---

## ⚙️ Advanced Usage & Tips

### Performance optimization
```bash
# Pre-build indexes once so all future runs are instant
DumpDetective load app.dmp

# Subsequent runs will reuse .bfs.idx and .idom.idx caches
DumpDetective heap-stats app.dmp             # runs in <1 second
DumpDetective dominator-tree app.dmp         # instant retained-size queries
```

### Save & replay reports (no dump file needed later)
```bash
# During incident, build and save report
DumpDetective analyze app.dmp --full --output incident.bin

# Later, convert to HTML or compare with another report
DumpDetective render incident.bin --output incident.html
DumpDetective diff incident.bin baseline.bin --output changes.html
```

### Multi-format output
```bash
# Generate multiple formats in one command
DumpDetective analyze app.dmp --full \
  --output report.html \
  --output report.json \
  --output report.md

# Or use --format for auto-naming
DumpDetective analyze app.dmp --full --format html --format json
```

### Configure analysis thresholds
Place `dd-thresholds.json` in your working directory to customize health score rules:
```json
{
  "CriticalThresholds": {
    "HeapWaste": 0.25,
    "DeadObjectsRatio": 0.10
  },
  "WarningThresholds": {
    "HeapWaste": 0.15,
    "DeadObjectsRatio": 0.05
  }
}
```

---

## ❓ Quick Reference

### When to use what?

| Scenario | Command | Why |
|---|---|---|
| **Service crashed, you have a dump** | `analyze --full` | One command, all insights, health score, prioritized findings |
| **Slow allocations or GC pauses** | `alloc-trace` or `gc-trace` | See allocation call stacks and GC timeline with timings |
| **High memory, don't know why** | `heap-stats` + `dominator-tree` | Overall heap shape, then exact retained-size paths |
| **Memory grew over time** | `trend-analysis` on multiple dumps | Compare heap snapshots over hours/days |
| **Thread deadlock suspected** | `deadlock-detection` + `thread-analysis` | Lock cycles and blocking call stacks |
| **Event handlers not unsubscribing** | `event-analysis` | Enumerate registered event handlers with retained sizes |
| **Correlate trace to dump** | `trace-dump-analyze` | Link CPU spikes / allocations to heap evidence |
| **Custom analysis** | Build a plugin | Inherit `ICommand`, run in `analyze --full` |
| **Browser page feels slow/janky** | `web-analyze` on a DevTools trace | JS heap, CPU hotspots, long tasks, GC, jank, input latency, network — all with a Call Stack traced back to your code |

### Common workflows

```bash
# Incident investigation
DumpDetective analyze app.dmp --full                      # Get overview + findings
DumpDetective dominator-tree app.dmp -o dominator.html   # Dig into top retained paths
DumpDetective trace-dump-analyze app.nettrace app.dmp    # Cross-source root cause

# Performance tuning
DumpDetective alloc-trace app.nettrace -o alloc.html     # Hot allocation call stacks
DumpDetective gc-trace app.nettrace -o gc.html           # GC pause analysis
DumpDetective heap-fragmentation app.dmp                 # Large-object heap waste

# Trend analysis
DumpDetective load app-before.dmp app-after.dmp          # Pre-build caches
DumpDetective trend-analysis app-before.dmp app-after.dmp
```

---

## ❓ FAQ & Troubleshooting

**Q: How long does analysis take?**  
A: First run builds indexes (~5–30s for small dumps, ~2–5 min for 15GB+ dumps). Subsequent runs are instant (<1s) because caches are reused.

**Q: What's the health score?**  
A: A 0–100 score reflecting heap health. Deducted for leaks, fragmentation, deadlocks, etc. Configurable via `dd-thresholds.json`.

**Q: Can I use DumpDetective on Linux?**  
A: Yes, for `.core` dumps (supported since v3.3.0). For WinDbg `.dmp`/`.mdmp` files, you need Windows.

**Q: How much memory does DumpDetective need?**  
A: Roughly 1–2x the dump size. A 15 GB dump needs ~20 GB RAM. Use `DumpDetective load` to pre-build caches if you're tight on resources.

**Q: Can I add my own analysis commands?**  
A: Yes! Drop a .NET plugin in `plugins/` or `~/.dumpdetective/plugins/`. See [Docs/Plugins.md](Docs/Plugins.md).

**Q: What cache files can I delete?**  
A: `.bfs.idx`, `.idom.idx`, and `.ddcache/` are safe to delete — they rebuild automatically. Keep the `.dmp` file.

**Q: Is my dump file kept secure?**  
A: Dumps are processed locally only. No data is sent anywhere. Analyze offline dumps confidently.

**Q: Why is the HTML report so large?**  
A: It's fully self-contained (all CSS, JS, data embedded). One file = one complete, shareable report.

**Q: How do I record a Chrome DevTools trace for `web-analyze`?**  
A: DevTools → Performance panel → check "Memory" if you want heap/listener trend data → Record → interact with the page → stop → Export. Pass the exported `.json` directly, or gzip it first (`.json.gz` works too).

---

## 📚 Full Documentation

- **[Docs/documentation.md](Docs/documentation.md)** — complete command reference, options, performance benchmarks, health score thresholds
- [Memory Analysis Guide](Docs/Memory-Guide.md) — deep-dive on dump commands with examples
- [Trace Analysis Guide](Docs/Trace-Guide.md) — deep-dive on trace commands with examples
- [Cache Inventory](Docs/cache.md) — understanding .bfs.idx, .idom.idx, .ddcache lifecycle and performance
- [Plugin System](Docs/Plugins.md) — write and install custom commands with full API reference
- [Architecture](Docs/Architecture.md) — project structure, dependency graph, design decisions

---

## 📝 License

[MIT](LICENSE)
