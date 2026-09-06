# Web Trace Analysis — Implementation Plan

Status: **feature-complete, docs consolidation pending**. Steps 1–10 are built and
validated against all three reference traces; only step 11 (folding this into the
permanent docs and retiring this file) remains. This document exists so the feature
survives a context reset — treat it as the source of truth for scope/ordering until
step 11 is done, then fold whatever's still relevant into `Trace-Guide.md` /
`Architecture.md` / `documentation.md` and delete or archive this file.

Seven commands ship today: `web-memory-leak`, `web-cpu-hotspots`, `web-long-tasks`,
`web-gc-pressure`, `web-network`, `web-jank`, `web-input-latency`, plus the `web-analyze`
rollup (health score, ranked Action Queue, "Look Here First", auto-play filmstrip,
`--with-plugins`, `--fail-on` CI gate). `diff`/`render` already work on saved web reports
with no new code.

### Progress against the build order below

- [x] Step 1 — `DumpDetective.Analysis.WebTrace` streaming parser (`ChromeTraceParser` +
      `JsonBufferCursor`) + `WebTraceContext`. Validated against all three reference
      traces: ~1.1–1.7s to stream a ~200MB/237k-event file, ~180MB peak working set.
- [x] Step 2 — cache layer, consolidated into one `.ddcache/<trace-name>/web/web.cache`
      file for now (not yet split into the five per-artifact files sketched below) —
      ~300KB–5MB cache from a 200MB source depending on screenshot count; second run of
      any command against the same file loads from cache in ~20ms instead of re-parsing.
      Format is versioned (`Version` const) — a schema change invalidates old caches
      automatically, no migration code needed.
- [x] Step 3 (complete) — `WebMemoryLeakCommand` (`web-memory-leak`) and
      `WebCpuHotspotCommand` (`web-cpu-hotspots`) implemented, registered, and confirmed
      working end-to-end against all three reference traces — reproduced the
      manually-found listener leak (133k listeners : 18k nodes, ~7.3:1) and the
      `short-unique-id.js` CPU hotspot (~40% of sampled CPU) automatically.
      **Source-map de-minification is done** (`SourceMapVlq` decoder + resolution wired
      into `ChromeTraceParser`) — confirmed resolving minified vite chunk names back to
      real `node_modules/devextreme/...` source paths (which also identified the actual
      UI library involved, previously unknown from the minified names alone).
- [x] Step 4 — Action Queue wiring: web `Finding` categories (`Web Memory`,
      `Web Listeners`, `Web Performance`, `Web Rendering`, `Web GC`) added to
      `ActionQueueBuilder.TargetCommandFor`; confirmed producing ranked P1/P2/P3 items
      matching the manually-found issues, via `web-analyze`'s Action Queue section.
- [x] Step 5 — `WebLongTaskCommand` (`web-long-tasks`) and `WebGcPressureCommand`
      (`web-gc-pressure`) implemented. `WebLayoutThrashCommand` was **not** built as a
      separate command — the forced-synchronous-layout finding it would have produced is
      folded into `web-cpu-hotspots`'s existing `Web Rendering` finding instead (same
      underlying data, no separate command needed for v1).
- [x] Step 6 — `WebAnalyzeCommand` (`web-analyze`) rollup: health score (0–100, deduction
      based, same shape as `HealthScorer`), ranked Action Queue (Now/Next/Watch), and a
      "Look Here First" top-3 pointer section, confirmed end-to-end (P1 → listener leak,
      P2 → node growth, P3 → CPU hotspot, matching priority order by design).
- [x] Step 7 — `IWebSubAnalyzer` extension interface (mirrors `ITraceSubAnalyzer`, minus
      the consumer/dispatch half — not needed since `WebTraceContext.Open` already
      reduces the file once). `WebCommandRegistry` (mirrors `TraceCommandRegistry`).
      Plugin wiring done: `PluginLoader` now derives `LoadedPlugin.WebSubAnalyzers` via
      `commands.OfType<IWebSubAnalyzer>()`, and `web-analyze --with-plugins` merges them
      in — same opt-in shape as `trace-analyze --with-plugins`. **Not yet done**: no
      reference plugin example exists yet exercising this (matching `Docs/PluginExample/`
      for the dump side) — first real plugin author will be the first real test of it.
- [x] Step 8 — `ReportFilmstrip` element (Core) + `HtmlSink` auto-playing flipbook
      (play/pause/seek, pure inline `<script>`, no video encoding or image-processing
      dependency) + `CaptureSink`/`JsonSink`/`BinSink`/`ReportDocReplay` wiring. Wired into
      `web-analyze` only (not the four standalone commands) using the actual embedded
      JPEG screenshots, downsampled to 200 frames max to keep HTML size bounded. Confirmed
      producing a valid playable filmstrip (2.3MB HTML, verified JPEG data). Non-HTML
      sinks use the interface's plain-text fallback (frame count only) rather than the
      "Markdown embeds one representative frame" refinement sketched in the design —
      deferred, low value vs. effort.
- [x] Step 9 — `WebNetworkCommand` (`web-network`), `WebJankCommand` (`web-jank`),
      `WebInputLatencyCommand` (`web-input-latency`) built and validated against the two
      reference traces that have network-capture data. Confirmed real findings: a 13.2s
      static-asset load, a 15% compositor frame-drop rate, and a 24.8s worst-case input
      latency — all independently consistent with the listener-leak/main-thread-blocking
      root cause the other analyzers already found. Parser extended with request
      correlation (Send/Receive/Finish by `requestId`), frame counters, and async
      begin/end pairing for `InputLatency::*` (matched by UTF-8 prefix, not a
      per-variant whitelist) — all within the existing single-pass rule.
      `ActionQueueBuilder.TargetCommandFor` extended with keyword routing across the
      now-7 web analyzers sharing the `Web Performance`/`Web Rendering` categories.
      Also fixed a real bug found while validating this: `web-analyze`'s Action Queue
      ordering used insertion order (first-analyzer-wins), not severity — now sorted by
      (severity, deduction) before bucketing, since unlike `HealthScorer`'s single-source
      findings, these come from independent analyzers with no inherent cross-analyzer order.
- [x] Step 10 (partial) — **`web-diff` needed no new code**: the existing `diff` command
      already operates on any two saved `.json`/`.bin` report files regardless of source
      (dump or trace), so `web-analyze trace.json.gz -o a.json` twice + `diff a.json
      b.json` already works — confirmed end-to-end. (Found and fixed a real bug in the
      process: `ReportFilmstrip` wasn't registered as a `[JsonDerivedType]` on
      `ReportElement`, so any report containing a filmstrip failed to serialize to
      JSON/Bin at all — now fixed.) CI gate **is built**: `web-analyze --fail-on
      critical|warning` exits 2 when a finding at or above that severity is present,
      confirmed exiting 2 on the reference trace's listener-leak finding.
- [x] Step 11 (partial) — `Docs/documentation.md` updated: new "Web Performance
      Commands" section (all 8 commands), Documentation Sections index entry, intro
      paragraph, and a Quick Start example. **Not done**: no dedicated `Web-Guide.md`
      mirroring `Trace-Guide.md`'s depth (per-command option references, worked
      troubleshooting playbooks), no per-command pages under `Docs/web/...` (the trace
      side links each command to a detail page — the web section currently doesn't),
      and `Docs/cache.md` / `Docs/Plugins.md` haven't been updated with the new web cache
      layer / `IWebSubAnalyzer` extension point. This file (`WebTrace-Plan.md`) should
      stay as the canonical reference until that deeper documentation pass happens.

### Post-ship fixes (found from real usage, not part of the original 11 steps)

- **Nav bar was broken in `web-analyze`**: `WebAnalyzeReport` wrapped each sub-analyzer's
  replayed `ReportDoc` in `BeginDetails`/`EndDetails`, but `Header()` — not `<details>` — is
  what the sidebar nav actually keys off (`data-nav-level`/`data-nav-group`). Fixed by
  copying `TraceAnalyzeCommand`'s exact pattern: navLevel:2 group headers (`Memory &
  Listeners`, `CPU & Rendering`, `Tasks & GC`, `Network & Responsiveness`) each followed
  by a plain `ReportDocReplay.Replay(doc, sink)` — no wrapping. Confirmed via the
  generated HTML's `data-nav-level`/`data-nav-group` attributes matching the trace-side
  shape exactly.
- **Long tasks were an opaque list** (duration/timestamp/pid/tid, no context). Fixed with
  real long-task attribution: the parser reconstructs absolute CPU-profiler-sample
  timestamps (samples only carry a delta from the previous sample) per `(pid, profiler
  id)`, buckets self-time into 50ms windows during the single pass, then — as a
  post-parse step over already-bounded in-memory data, not a second file read — sums the
  buckets each long task's window overlaps and attributes it to the top hotspot there.
  `web-long-tasks`'s table now shows "Likely Cause" + de-minified location per task, and
  a new finding fires when one function is the likely cause of most long tasks. Confirmed
  against the reference trace: 70 of 92 long tasks attributed to `short-unique-id.js` —
  directly corroborating the CPU-hotspot finding, not just duration numbers in isolation.
  Cache bumped to v4 for the new attribution fields.
- **Long tasks needed a "what do I fix first" view, not just a per-task list.** Added
  `WebLongTaskRootCause` — every long task rolled up by attributed cause and ranked by
  *total blocked time* (not task count: three 2-second freezes from one function outrank
  ten 60ms ones from another). Rendered as a "Root Causes" table above the raw task list.
  Confirmed on the reference trace: `short-unique-id.js` is responsible for **86% of all
  main-thread blocking time** (28,947ms across 70 tasks) — a single, unambiguous, ranked
  answer to "what should I fix first", instead of scanning 92 individual rows. Added a
  matching Critical finding with remediation advice that branches on whether the top
  cause is application JS (check call count in web-cpu-hotspots) vs. native browser work
  like layout/GC (points at the right command instead of telling the user to hunt for a
  JS function that doesn't exist).
- **A leaf function's own name often doesn't say which feature is responsible.** Asked
  to check whether "grid" files show up in long-task attribution against
  `Web_DMS_EV_IB_Maintenance_Memory.json.gz`, found `grid_core/views/m_columns_view.js`
  already present (9% of blocking time), but the #1 cause (86%, `getCSSProperty` in
  `m_size.js`) wasn't grid-named — because it's a shared size-measurement utility called
  from everywhere. Manually walking the V8 CPU profiler's `node.parent` chain (present in
  the raw JSON but previously discarded) confirmed it: `getCSSProperty ← get ←
  getComponentThickness ← getSize ← elementSize ← elementSizeHelper ← getWidth ← getWidth
  ← width ← _moveSeparator` — `_moveSeparator` is DevExtreme's grid column-resize handler.
  Built this into the tool instead of leaving it a one-off manual trace: `WebCpuHotspot`
  now carries a `CallChain` (nearest 10 callers, computed once per unique hotspot from the
  already-parsed node graph — no second pass), surfaced as a "Called From" table in both
  `web-cpu-hotspots` and `web-long-tasks`' Root Causes. Cache bumped to v6.
- **A real miss, and a hard limit worth knowing about**: the user had already found and
  fixed the actual root cause themselves — `RepaintDataGrid` in `tabDataGridSelector.ts`
  — and our report never surfaced it. Investigated why: the function *is* in the CPU
  profile (confirmed via its node id), but with only ~11ms self-time across 38 samples,
  because its only recorded child is `setTimeout` — it schedules the real (expensive)
  work asynchronously and returns immediately. Checked whether the actual hot code
  (`getCSSProperty`, `grid_core/m_columns_view.js`) is a call-tree descendant of
  `RepaintDataGrid` at all: **0 out of dozens of sampled occurrences were** — because
  V8's CPU profiler's `node.parent` chain only reflects the synchronous call stack; it
  does not, and cannot, bridge a `setTimeout`/promise boundary. This means no amount of
  self-time, inclusive-time, or call-chain analysis over this data can causally link a
  scheduling function to the deferred work it triggers — Chrome DevTools' own Performance
  panel has the identical blind spot for the same reason, this isn't a fixable gap in our
  attribution logic. What *is* fixable, and now built: `WebCpuHotspotRow.IsApplicationCode`
  (first-party path heuristic: no `node_modules`/`.vite/deps/`) plus a new "Application
  Code" section in `web-cpu-hotspots` listing every first-party function found anywhere
  in the profile regardless of self-time rank — so a thin trigger function like this is
  never silently buried below the top-25/40 cutoff again, with a finding and explicit
  in-report explanation of the setTimeout-boundary limitation so nobody mistakes low
  self-time for "not the cause".
- **Closed the loop with a timing-correlation heuristic.** Told the tool still didn't make
  the link to `tabDataGridSelector.ts` obvious enough. Built `AttributePossibleTriggers`:
  for every long task, binary-searches a sorted, bounded list of first-party-code sample
  timestamps (collected during the same single pass, not a second read) for the nearest
  one before the task started, within a 5s window — explicitly labeled a timing
  correlation, never a call-tree fact, since that's genuinely all it can be given the
  setTimeout-boundary limitation above. First cut picked only the single nearest sample,
  which surfaced an unrelated browser extension's content script as a false positive
  (fixed by excluding `chrome-extension://` URLs from the "first-party" heuristic) and,
  separately, missed `tabDataGridSelector.ts` because closer application samples existed
  for the specific tasks checked. Fixed by adding a second, tighter mechanism —
  `PossibleTriggerCluster`: every distinct first-party function seen in the 1 second
  before a task started (not just the nearest one), since a real cause is often a *burst*
  of several setup/render functions firing together, not one isolated call. Confirmed
  against the reference trace: `RepaintDataGrid (tabDataGridSelector.ts:1)` now appears
  directly in both the per-task table (for the single longest task, 3,164ms) and the 86%
  dominant Root Cause row, alongside the other grid-setup components (`DxFormGrid`,
  `DxGridColumns`, `dxColumn.ts`) that fired in the same burst. Cache bumped to v10.
- **Call chains were still hard to read — switched to the app's own established widget
  instead of inventing another text format.** First fix (indented `sink.Text` lines) was
  more readable than a crammed table cell but still wasn't right. Checked how the rest of
  the app displays a linear call chain: `CpuTraceReport`'s "Hot Path" section builds a
  flat, ordered list of single-child `CallTreeNode`s and renders it via
  `sink.CallTree(...)` — the same interactive, indented tree widget used everywhere else
  in this app for call-tree data (collapsible in HTML, plain indented list elsewhere).
  Added `WebCallChainHelper.BuildChain` to convert our "nearest ← ... ← furthest" call-chain
  breadcrumb into that exact shape (root/furthest-caller first, hotspot itself last — same
  order `CpuTraceAnalyzer` builds its hot path in) and switched both `web-cpu-hotspots`'s
  "Called From" and `web-long-tasks`' Root Causes "Called From" to use it. The flat,
  non-hierarchical "Your Code Seen Nearby" lists (which aren't a real call chain — several
  independent functions seen nearby, not caller/callee) were switched to a plain
  single-column `sink.Table` instead, since implying a call hierarchy there would be
  misleading; a table is this app's existing convention for "here's a ranked/listed set of
  rows", not a novel format.
- **Asked directly whether the report was actually good, not just individually-correct
  pieces.** Checked honestly: `web-analyze` on the reference trace was 55 `<table>`
  elements and 16 `CallTree` widgets (each with its own Expand/Collapse toolbar) stacked
  on one page. The nav/chapter structure itself was fine (verified: Health Score → Look
  Here First → Action Queue → Filmstrip, then four clean grouped chapters), but two
  sections were pure repetition rather than density: `web-cpu-hotspots`'s "Called From"
  rendered a full CallTree widget for 10 hotspots when the ranking already puts the
  signal in the top few; `web-long-tasks`' "Your Code Seen Nearby — per task" rendered one
  full mini-table per task (up to 30) even though many tasks in the same burst share the
  *identical* nearby-code set — checked directly: 25 tasks with a cluster collapsed to
  only 11 distinct values. Fixed both: hotspot caller chains capped at top 5 (was 10), and
  the per-task list now groups by distinct cluster content — each unique burst shown once
  with a task count and total blocked time, not once per task. Table count on the same
  report dropped 55→35, CallTree widgets 16→11.
- **Asked why "Called From" and "Application Code" weren't linked.** Found the real gap:
  `BuildCallChain` only ever stored each caller's bare function *name*, discarding its
  URL/location — so a caller could never be checked against the first-party heuristic at
  all, and the two sections had no way to reference each other. Fixed at the source: each
  chain entry now carries its de-minified location and raw URL too (delimited encoding,
  cache bumped to v11), so a caller can be checked inline — any first-party caller in a
  chain is now labeled `★ (your code)` directly in the `CallTree` widget, no
  cross-referencing needed. Checked whether that alone was enough by dumping the real
  data: it wasn't — the top 5 hotspots by self-time in the reference trace have *zero*
  application callers within a 10-level chain (confirmed: they're pure vendor-to-vendor
  call graphs), while 138 of 1,342 total hotspots do have one, just not among the top 5.
  Added the actual bridge: a new "Hotspots Traced to Your Code" section/list — vendor
  hotspots whose chain *does* reach into application code, ranked by their own self-time
  independent of the top-N cut — so the connection isn't buried in the ranking's blind
  spot. Confirmed on the reference trace: rows explicitly showing e.g. a React hotspot in
  `react-jsx-dev-runtime.js` called from `SideBar.tsx`, `layout.tsx`, `DxFileUploader.tsx`.

## Why

DumpDetective currently analyzes two input kinds: heap dumps (`.dmp`/`.mdmp` via ClrMD)
and .NET traces (`.nettrace`/`.etl` via TraceEvent). Chrome DevTools "Performance" panel
recordings (`.json`/`.json.gz`, Chrome Trace Event Format) are a third, unrelated kind —
browser/JS performance, not .NET runtime — that the app doesn't understand at all today.

This was prompted by three real recordings that already point at a concrete, findable
bug (see "Reference sample findings" below): a JS event-listener leak plus a CPU hotspot
in a specific bundled file. The goal is to make that kind of diagnosis automatic and to
present it so a web developer with no profiling background can act on it immediately.

## Format background

- Gzip'd JSON. Top level: `{"metadata": {...}, "traceEvents": [...]}`.
- `metadata.sourceMaps[]` — **source maps are embedded directly in the trace file**
  (Chrome's "Enhanced Traces" export). No network fetch needed to de-minify.
- `traceEvents[]` — standard Trace Event Format (`ph`, `cat`, `name`, `ts`, `pid`, `tid`,
  `args`). Relevant event families:
  - `UpdateCounters` (`disabled-by-default-devtools.timeline`) — periodic samples of
    `jsHeapSizeUsed`, `documents`, `nodes`, `jsEventListeners`.
  - `Profile` / `ProfileChunk` (`disabled-by-default-v8.cpu_profiler`) — V8 CPU profiler:
    `cpuProfile.nodes[]` (call frames: `functionName`, `url`, `lineNumber`, `columnNumber`,
    `scriptId`, `parent`) + `cpuProfile.samples[]` (node id per sample) + top-level
    `timeDeltas[]` (µs per sample, sibling of `cpuProfile`, **not** nested inside it).
  - `RunTask` (`disabled-by-default-devtools.timeline`) — main-thread task spans; duration
    from `dur`/`ts`, useful for >50ms "long task" detection.
  - GC events (`devtools.timeline,disabled-by-default-v8.gc` category).
  - `Screenshot` (`disabled-by-default-devtools.screenshot`) — `args.snapshot` is a
    base64 JPEG, one roughly per frame. `args.expected_display_time`/`ts` orders them.

## Reference sample findings (validates the approach)

Computed by hand against the three supplied traces — cited here so the eventual
analyzers have a known-good target to reproduce:

| Trace | max `jsEventListeners` | max DOM `nodes` | ratio |
|---|---|---|---|
| `Web_DMS_EV_IB_Maintenance.json.gz` (the one that solved the issue) | 133,431 | 18,248 | ~7:1 |
| `Web_DMS_EV_IB_Maintenance_Memory.json.gz` | 122,780 | 20,790 | ~6:1 |
| `Trace-20260904T182452.json.gz` | 148,872 | 12,369 | ~12:1 |

A healthy page is nowhere near 1 listener per node, let alone 6–12 — strong signal of
listeners being rebound without cleanup.

CPU self-time aggregated from `ProfileChunk` samples on the first trace:

```
36,513 ms  short-unique-id.js                         (~49% of all sampled CPU time)
12,075 ms  ready_callbacks-*.js  getCSSProperty
10,437 ms  (native) get offsetWidth                    <- forced synchronous layout
```

`short-unique-id.js` at 49% of CPU is not something an ID generator should ever cost;
combined with the listener/node ratio this points at a widget/grid minting a fresh ID +
rebinding a listener per row on every re-render. The `offsetWidth` line is a second,
independent finding: reading layout properties in a loop without batching (layout
thrashing).

## Streaming parser — no whole-file load

These files run 200–230MB uncompressed for a ~13-minute recording; loading that into a
`JsonDocument`/POCO tree (as the exploratory analysis for this plan did in Python) is not
acceptable in the shipped tool. Rule: **the parser never materializes the full
`traceEvents` array in memory at once** — same spirit as "single heap walk" for dumps.

- `GZipStream` (or raw `FileStream` if uncompressed) wrapped directly into a
  `Utf8JsonReader`-driven reader operating over a `PipeReader`/buffered `ReadOnlySequence<byte>`
  — the standard pattern for streaming an array too large to buffer (see .NET docs
  "Read large JSON payloads without allocating one buffer"). Never
  `JsonSerializer.Deserialize<TraceFile>(stream)`.
- Walk `metadata` first (small, safe to fully deserialize via source-gen — it's KB-sized
  except `sourceMaps[].sourceMap.sourcesContent`, see below), then stream `traceEvents[]`
  element-by-element: for each element, source-gen-deserialize only into the minimal typed
  shape a registered consumer needs (`JsonSerializer.Deserialize<TEvent>(ref reader, ctx.TEvent)`
  per array item), discard immediately, advance. Peak memory is O(one event), not O(file).
- `metadata.sourceMaps[].sourceMap.sourcesContent` (the original, unminified source text —
  seen at multiple KB per file, dozens of files) is the single biggest incidental cost in
  the metadata block and isn't needed for analysis (only `mappings`/`sources`/`names` are).
  Skip deserializing it — `Utf8JsonReader.TrySkip()` over that property instead of
  allocating the strings.
- One pass, many consumers: mirrors the existing `IHeapObjectConsumer` /
  `ITraceEventConsumer` rule. A single `WebTraceEventDispatcher.Dispatch(stream, consumers)`
  drives every registered `IWebTraceEventConsumer` off **one** stream read; a new metric
  is a new consumer, never a second full-file parse. This is the rule to enforce in code
  review the same way "single heap walk" already is.
- Cheap, format-level pre-filtering during the single pass: category strings are
  interned/compared without allocating (`ReadOnlySpan<byte>` comparisons against known
  category byte sequences) before bothering to deserialize `args` for events no consumer
  cares about — most of the 237k events in the sample traces are draw/frame/compositor
  bookkeeping (`cc,benchmark,disabled-by-default-devtools.timeline.frame`) irrelevant to
  every planned analyzer, so skipping their `args` payload entirely is most of the win.

## Caching strategy

Same shape as the existing `.ddcache/<dump-name>/` layer (`Docs/cache.md`), so the mental
model transfers: build derived artifacts once on first touch, validate by source-file
size+mtime on every later run, rebuild silently if stale, blow away with `close`.

`<trace-dir>/.ddcache/<trace-name>/web/`:

| File | Built from | Why cache it |
|---|---|---|
| `counters.bin` | `UpdateCounters` samples | Tiny (thousands of rows × few numbers) but otherwise means re-streaming the whole file for every memory-trend command run |
| `cpu-profile.bin` | `ProfileChunk` nodes + samples + timeDeltas, already reduced to `(url, function, line) → self-time`/call-tree | The expensive part (biggest event volume); reduced form is orders of magnitude smaller than the raw chunks |
| `long-tasks.bin` | `RunTask` spans over a min-duration floor | Filtered at cache-build time, not at query time |
| `gc-events.bin` | GC begin/end spans | Small |
| `screenshots.blob` + `screenshots.idx` | `Screenshot.args.snapshot` | Base64 is decoded to raw JPEG bytes **once**, written sequentially to a plain seekable blob file with an `(timestampMs, offset, length)` index — the filmstrip command then reads only the frames it needs directly by offset, never touching the source `.json.gz` or holding all frames in memory |
| `sourcemaps.bin` | `metadata.sourceMaps[].sourceMap.mappings` | VLQ decoding is the expensive part of de-minification; decode once into a sorted-by-generated-position lookup table per source file so every later hotspot/long-task resolution is a binary search, not a re-decode |
| `manifest.json` | source file size + mtime + a schema version | Same invalidation check as every other `.ddcache` artifact — reject and rebuild silently on mismatch |

Net effect: the 200MB stream-parse happens once per trace file, ever (until it changes or
`close` is run); every command after that — and every chapter of `web-analyze --full` —
reads from the small binary caches instead. `load`-equivalent behavior: the first command
run against a `.json.gz` builds the cache layer, exactly like `load` does for dumps today.
Document this layer in `Docs/cache.md` once implemented (new "5) Web Trace Cache" section).

## Additional features worth building in

Beyond the six chapters already planned, the trace format supports more that's high value
and cheap given the streaming pass already visits these events:

- **Network waterfall** (`WebNetworkCommand`) — `ResourceSendRequest`/`ResourceReceiveResponse`/
  `ResourceFinish` events give a request timeline; flag render-blocking requests and slow
  responses overlapping the flagged problem windows.
- **Jank / dropped frames** (`WebJankCommand`) — `DroppedFrame`/`BeginFrame` events give an
  effective FPS-over-time series; report the worst stretches, correlated with the CPU
  hotspot active at that time.
- **Input responsiveness** (`WebInputLatencyCommand`) — `InputLatency::*` events give
  click/scroll-to-response timing; flag interactions slower than a threshold and attribute
  them to the `RunTask`/CPU-profile activity in that window (an "Interaction to Next Paint"
  style finding).
- **Third-party attribution** — every hotspot/long-task finding already carries a `url`;
  bucket by origin (same-origin/first-party bundle vs. CDN/vendor/analytics host) so the
  report can separate "your code" from "a library you depend on" at a glance — this is
  what actually tells a developer whether the fix is theirs to make.
- **Call-count, not just self-time** — the `short-unique-id.js` finding in the reference
  sample is as much about *call count* as self-time (a function called thousands of times
  per render is a smell even at low per-call cost); track sample-derived call counts per
  `(url, function)` alongside self-time so "called 40,000 times" shows up even when
  self-time alone wouldn't rank it highly.
- **Trace-vs-trace diff** — `ReportDiffer`/`ReportDocReplay` already do element-level
  `ReportDoc` diffing for the dump side; a `web-diff` (or reusing `diff` once both docs are
  `ReportDoc`s) compares a before/after pair — "did the listener count actually come down
  after the fix" — with no new diff engine needed, just two `web-analyze` runs.
- **CI gate mode** — `web-analyze --fail-on critical` (or reusing whatever exit-code
  convention `analyze`/`trace-analyze` already use, if any) so a regression in listener
  count/CPU hotspot fails a build instead of only being visible in a report someone has to
  open.
- **Reuse `ReportCallTree` for hotspots/long-tasks** — Core already has a `ReportCallTree`
  element (used by dump call-tree renderers); the aggregated CPU self-time tree and
  long-task attribution should render through that existing element instead of a new one —
  the only genuinely new `ReportElement` needed for this whole feature is the filmstrip.

## Architecture fit

Does **not** reuse `DumpDetective.Analysis.Trace` / `Commands/Trace` — those are built
entirely around `Microsoft.Diagnostics.Tracing.TraceEvent` (.NET ETW/EventPipe: GC, JIT,
Kestrel, SQL, sockets). Chrome traces are a self-contained JSON format with no TraceEvent
involvement, so they get their own project, same dependency rule as the existing
analysis projects (depends only on Core):

```
DumpDetective.Analysis.WebTrace   (new, depends only on Core)
DumpDetective.Commands/Web/       (new, depends on Core + Analysis.WebTrace + Reporting)
```

### `DumpDetective.Analysis.WebTrace`

- Chrome Trace Event Format parser: gzip-sniff + JSON, `[JsonSerializable]` AOT source-gen
  models limited to the shapes actually consumed (`UpdateCounters` args, `ProfileChunk`
  nodes/samples/timeDeltas, `RunTask` spans, GC events, `Screenshot` args, `metadata.sourceMaps`).
  No reflection-based `JsonSerializer`, per repo convention.
- VLQ source-map decoder (small, self-contained, no external dependency) — turns
  `(url, line, col)` into `(originalFile, originalLine, originalName)` when a matching
  entry exists in `metadata.sourceMaps`.
- `WebTraceContext` — parsed-once view analogous to `DumpContext`, handed to every command.
- `WebHealthScorer` — pure POCO, mirrors `HealthScorer`: snapshot + thresholds → `Finding`s
  + 0–100 score. No parser types leak into it; unit-testable without a real trace file.

### Findings & priority (reuses existing machinery, no new concept)

`ActionQueueBuilder` (`DumpDetective.Core/Utilities/ActionQueueBuilder.cs`) already turns
`Finding` records into ranked `ActionItem`s (Now/Next/Watch buckets, P1/P2… score) with no
ClrMD/TraceEvent coupling. Web analyzers just emit `Finding`s the same way `HealthScorer`
does and the triage UX falls out for free. Changes needed there are additive only:

- New `Finding.Category` values: `"Web Memory"`, `"Web Listeners"`, `"Web Performance"`,
  `"Web Rendering"`, `"Web GC"`.
- Extend `ActionQueueBuilder.TargetCommandFor`'s switch with cases for the new categories
  mapping to the new command names below (same pattern as the existing Memory/Leaks cases).

### New commands (`Commands/Web/`, one `ICommand` each, mirrors `Commands/Trace/`)

1. `WebMemoryLeakCommand` — trend `jsHeapSizeUsed` / `nodes` / `jsEventListeners` over
   time; monotonic-vs-sawtooth slope check; flag listener:node ratio outliers.
2. `WebCpuHotspotCommand` — self-time by `(url, functionName, line)` aggregated from CPU
   profile samples, de-minified via the source-map decoder, ranked.
3. `WebLongTaskCommand` — `RunTask` spans over a threshold (default 50ms), attributed to
   the active call frame during that window.
4. `WebGcPressureCommand` — Major/Minor GC frequency/duration correlated with heap-growth
   windows.
5. `WebLayoutThrashCommand` — forced synchronous layout detection (`offsetWidth`/
   `getBoundingClientRect`/etc. self-time outside a layout phase).
6. `WebAnalyzeCommand` (`web-analyze --full`) — rollup across the above, following the
   existing `analyze --full` LPT-scheduled parallel pattern; produces the health score +
   Action Queue + "look here first" pointer.
7. `WebNetworkCommand`, `WebJankCommand`, `WebInputLatencyCommand` — network waterfall,
   dropped-frame/FPS, and input-responsiveness findings (see "Additional features" above);
   same `ICommand` shape, added once the core five are proven out.

### Extensibility — this is meant to be a plugin surface, not a closed set

The whole point of enumerating this many analyzer ideas above is that nobody should have
to wait on us to add the next one. Mirror the existing `ITraceSubAnalyzer` /
`ICommandHeapContributor` / `ICommandCachePin` extension model exactly, rather than
inventing a new one:

- `IWebSubAnalyzer` (parallel to `ITraceSubAnalyzer`): `Key`, `SectionTitle`,
  `Run(WebTraceContext, ...)`, plus the same consumer-based single-pass option —
  `SupportsConsumer` / `CreateConsumer()` / `CompleteFromConsumer()` — but wired to
  `IWebTraceEventConsumer` and the one `WebTraceEventDispatcher.Dispatch` pass instead of
  `ITraceEventConsumer`/`TraceEventDispatcher`. A plugin analyzer that needs a second
  metric off the same stream adds a consumer; it never triggers a second parse of the
  200MB file, same rule as the host code.
- Plugins get their derived data from the cache layer, not the raw file: expose
  `WebTraceContext` accessors for the cached artifacts (`counters.bin`, `cpu-profile.bin`,
  the decoded source-map lookup, etc.) so a third-party analyzer plugging into an
  already-cached trace pays zero re-parse cost, exactly like `ICommandCachePin` today lets
  dependent commands share pinned dump analysis instead of recomputing it.
- Registration path is the existing one, unchanged: a plugin `.dll` dropped into
  `<exe dir>/plugins/` implementing `IPluginManifest` + `ICommand` (for a standalone
  sub-command) and/or `IWebSubAnalyzer` (to fold into `web-analyze --full --with-plugins`,
  matching today's `trace-analyze --with-plugins` convention) — no new loader, no new
  `AssemblyLoadContext` handling, no new manifest format.
- Everything in "Additional features worth building in" above (network waterfall, jank,
  input latency, third-party attribution, call-count ranking) is exactly the kind of thing
  that should be buildable as a plugin using this surface, not hardcoded into the host —
  build the first couple of them (`WebNetworkCommand`, `WebJankCommand`) in-tree as the
  reference implementation/proof of the extension API, same way `Docs/PluginExample/` is
  a working reference today, then document the pattern in a new `Docs/WebTrace-Plugins.md`
  (or a section added to `Docs/Plugins.md`) once it's proven.

### Report additions

- New `ReportElement` in Core: `ReportFilmstrip` — ordered `{timestampMs, base64Jpeg}`
  frames plus a highlighted sub-range. Built from `Screenshot` events, restricted to the
  windows a finding actually flagged (biggest heap-growth burst, long-task cluster, CPU
  hotspot window) rather than the whole recording, to keep HTML size sane.
- `HtmlSink`: renders `ReportFilmstrip` as a `<canvas>`/`<img>` flipbook driven by a small
  inline `<script>` that auto-advances frames fast — a "replay" of what was on screen when
  the problem happened. Pure HTML/CSS/JS, zero new dependencies (no ffmpeg/video encoding,
  no image-processing library), fully AOT-safe since it's base64 passthrough.
- Other sinks degrade gracefully: Markdown embeds one representative frame as a data-URI
  image with a note; JSON/Bin carry the frame data through unchanged since `ReportDoc` is
  already fully serializable.
- Every hotspot/long-task/leak `Finding` carries its de-minified file:line in `Detail`, and
  the top 3 ranked pointers get a dedicated "Look here first" alert at the top of the
  report — the answer to "which file do I look at", not something buried in a table.

### Chapter list for `web-analyze`

1. Health score + Action Queue (Now/Next/Watch — same triage UX as `analyze`)
2. Look here first — top 3 de-minified file:line pointers, ranked
3. Memory — heap/DOM-node/listener sparklines + leak verdict
4. CPU hotspots — self-time by de-minified (file, function), ranked
5. Long tasks — >50ms tasks attributed to the active call frame
6. Rendering — forced-layout/reflow thrashing detection
7. GC pressure — Major/Minor GC frequency vs. heap-growth correlation
8. Network (once built) — waterfall + render-blocking/slow-request findings
9. Jank & input latency (once built) — dropped-frame stretches, slow interactions
10. Filmstrip (HTML only) — auto-play replay of the flagged windows

### CLI wiring

- File-kind sniffing in the CLI entry point (gzip magic + `{"traceEvents"` vs. nettrace
  magic vs. ETL header) so a `.json`/`.json.gz` Chrome trace, a `.nettrace`, and an `.etl`
  all "just work" from a positional arg or `DD_DUMP` without a separate flag.
- New `ICommand.Kind` bucket (`Web`, alongside existing `Memory`/`Trace`) or a
  sub-discriminator on `Trace` — decide when `CommandRegistry` wiring is implemented;
  whichever keeps `analyze --full` / `trace-analyze --full` iteration untouched.

### Tests

No existing scenario-dump framework applies (can't spawn a browser from
`DiagnosticScenarios`), and there's currently no fixture-based test coverage for the
`.nettrace`/`.etl` commands either to model against. Plan: check in one trimmed sample
trace (strip `sourcesContent`, dedupe oversized arrays) as a fixture under
`DumpDetective.Tests/TestData/WebTraces/`, and write direct parser/analyzer unit tests
against it — plus pure-POCO unit tests for `WebHealthScorer` with synthetic snapshots
(no fixture needed), matching how `HealthScorer` is tested today.

A streaming parser is easy to accidentally regress back into "just buffer it" — add a
regression test that asserts peak working set stays within a fixed multiple (e.g. 5×) of
the compressed file size while parsing the checked-in fixture, so a future edit that
reintroduces `JsonDocument.Parse(wholeStream)` fails CI instead of only showing up as a
slow/OOM run against a real 200MB trace.

## Build order

1. `Analysis.WebTrace` streaming parser (gzip + `Utf8JsonReader`, source-gen event models,
   `WebTraceEventDispatcher` single-pass-many-consumers) + `WebTraceContext`.
2. `.ddcache/<trace-name>/web/` cache layer (`counters.bin`, `cpu-profile.bin`,
   `sourcemaps.bin`, `screenshots.blob`/`.idx`, `manifest.json` invalidation) — built once,
   right after the parser, so every command from here on reads cache, not the raw file.
3. `WebMemoryLeakCommand` + `WebCpuHotspotCommand` (source-map decoder needed here) —
   highest value, would have caught everything found in the reference samples above.
4. Action Queue wiring (`Finding` categories + `ActionQueueBuilder.TargetCommandFor` cases).
5. `WebLongTaskCommand`, `WebGcPressureCommand`, `WebLayoutThrashCommand`.
6. `WebAnalyzeCommand` rollup + "look here first" pointer assembly.
7. `IWebSubAnalyzer` extension interface + plugin wiring (`web-analyze --with-plugins`).
8. `ReportFilmstrip` element + `HtmlSink` flipbook rendering.
9. `WebNetworkCommand`, `WebJankCommand`, `WebInputLatencyCommand` — built against the
   `IWebSubAnalyzer` surface from step 7, doubling as its reference implementation.
10. `web-diff` (reuse `ReportDiffer`) + CI gate flag (`--fail-on`).
11. Docs: fold into `Trace-Guide.md`/`documentation.md`/`Docs/cache.md`/`Docs/Plugins.md`,
    retire this file.
