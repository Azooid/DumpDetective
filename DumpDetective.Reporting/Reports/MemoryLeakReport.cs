using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class MemoryLeakReport
{
    public void Render(MemoryLeakData data, IRenderSink sink, int top = 30, bool includeSystem = false)
    {
        // ── Heap Snapshot section (KVs + gen2/LOH alerts) ─────────────────────
        sink.Section("Heap Snapshot");
        sink.Explain(
            what: "Current state of the managed heap: generation sizes, fragmentation, and top types by count. " +
                  "This is the raw evidence for the memory leak investigation.",
            why:  "The relative sizes of Gen0/Gen1/Gen2 reveal the lifecycle of objects. " +
                  "Objects that survive multiple GC cycles promote to higher generations. " +
                  "If Gen2 keeps growing, objects are accumulating in long-lived memory instead of being collected.",
            bullets:
            [
                "Gen2 > 50% of heap → objects surviving multiple GC cycles — strong leak signal",
                "LOH growing → large objects (buffers, arrays, datasets) not being released",
                "Very high total object count → object creation rate significantly exceeds collection rate",
            ]);
        RenderHeapSnapshot(sink, data, top);

        // ── Step 2 — Suspect Types ─────────────────────────────────────────────
        sink.Section("Step 2  —  Suspect Types");
        sink.Explain(
            what: "Types with unusually high instance counts or large total memory footprints. " +
                  "These are candidate leak types — objects accumulating beyond expected steady-state levels.",
            why:  "A type with thousands of instances when you expect dozens is a strong leak indicator. " +
                  "The presence of the type alone doesn't confirm a leak — the GC root chain (Step 4) " +
                  "is needed to confirm why instances cannot be collected.",
            bullets:
            [
                "Look for your own application types (service classes, models, DTOs) at high counts",
                "System types (Task, CancellationTokenSource, Timer) at high counts indicate framework-level leaks",
                "Types growing across dumps (use trend-analysis) confirm accumulation vs. transient spike",
            ],
            action: "The types in this section are suspects, not confirmed leaks. Proceed to Step 4 to trace GC roots.");
        RenderCountSuspects(sink, data.CountSuspects, data.MinCount, includeSystem);
        RenderSizeSuspects(sink, data.SizeSuspects);

        // ── Step 3 — Accumulation Pattern Checks ──────────────────────────────
        sink.Section("Step 3  —  Accumulation Pattern Checks");
        sink.Explain(
            what: "Heuristic checks for common leak patterns: collections growing beyond expected size, " +
                  "static collections with many entries, and type counts suggesting per-request object accumulation.",
            why:  "Many memory leaks follow recognizable patterns. Static dictionaries that grow without bounds, " +
                  "event handlers that never unsubscribe, and per-request objects stored in long-lived caches " +
                  "are the most frequent root causes in .NET applications.",
            bullets:
            [
                "Collections with > 10,000 entries → unbounded growth in a cache or queue",
                "Static/singleton types accumulating → long-lived objects retaining short-lived objects",
                "High ratio of service/repository types → DI container scope mismatch (singleton holding scoped)",
            ]);
        RenderAccumulationPatterns(sink, data);

        // ── Findings ───────────────────────────────────────────────────────────
        sink.Section("Findings");
        RenderFindings(sink, data);

        // ── Step 4 — GC Root Chains ────────────────────────────────────────────
        sink.Section("Step 4  —  GC Root Chains  (gcroot simulation)");
        sink.Explain(
            what: "For each suspect type, traces the reference chain from the object back to the root that prevents collection. " +
                  "The root is a GC handle (static field, thread local, or pinned handle) — the actual source of the leak.",
            why:  "Knowing that a type has many instances does not tell you WHY they can't be collected. " +
                  "The GC root chain shows the exact reference path. The last entry in the chain is the retention root — " +
                  "that is where code changes need to be made.",
            bullets:
            [
                "HandleTable root → a GC handle (usually a static or long-lived field) is holding the object",
                "Thread root → the object is on a thread's stack or in a local variable of a running method",
                "Chain: HandleTable → Cache<T> → List<T> → T → means the Cache holds the list which holds your type",
                "Short chain (1-2 hops) → direct retention, easiest to fix",
                "Long chain (10+ hops) → indirect retention through complex object graph",
            ],
            action: "The last entry in the chain is the root. Remove the reference at the root, " +
                    "make the cache entry expiring, or ensure the Dispose() path clears the collection.");
        if (data.RootChains.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                "No suspect types to trace — nothing above the Step 2 threshold.",
                advice: "Lower --min-count to surface more candidates, or use --include-system.");
        }
        else
        {
            sink.Alert(AlertLevel.Info,
                $"Tracing root chains for top {data.RootChains.Count} suspect type(s).",
                detail: "The chain reads bottom-up: the last entry is the ROOT preventing GC collection. " +
                        "A typical chain looks like: HandleTable \u2192 Object[] \u2192 Cache \u2192 List<T> \u2192 T[] \u2192 T. " +
                        "The root type (static field, GC handle, or thread local) is where you need to act.");
            RenderRootChains(sink, data);
        }

        // ── Next Steps ────────────────────────────────────────────────────────
        sink.Section("Next Steps");
        sink.KeyValues([
            ("Targeted root trace (single type)", "gc-roots <dump> --type \"<TypeName>\""),
            ("All static field roots",            "static-refs <dump>"),
            ("All instances of a type",           "type-instances <dump> --type \"<TypeName>\""),
            ("Event handler leaks",               "event-analysis <dump>"),
            ("Timer object leaks",                "timer-leaks <dump>"),
            ("Finalizer queue backlog",           "finalizer-queue <dump>"),
            ("Compare two dumps over time",       "trend-analysis <dump1> <dump2> --full"),
        ]);
    }

    private static void RenderHeapSnapshot(IRenderSink sink, MemoryLeakData data, int top)
    {
        long totalHeap = data.TotalHeapSize;
        static double Pct(long part, long total) => total > 0 ? part * 100.0 / total : 0;

        double gen2Pct = Pct(data.Gen2Total, totalHeap);
        sink.KeyValues([
            ("Total managed heap",  DumpHelpers.FormatSize(totalHeap)),
            ("Generation 0",        $"{DumpHelpers.FormatSize(data.Gen0Total)}  ({Pct(data.Gen0Total, totalHeap):F1}%)"),
            ("Generation 1",        $"{DumpHelpers.FormatSize(data.Gen1Total)}  ({Pct(data.Gen1Total, totalHeap):F1}%)"),
            ("Generation 2",        $"{DumpHelpers.FormatSize(data.Gen2Total)}  ({gen2Pct:F1}%  of heap)"),
            ("Large Object Heap",   $"{DumpHelpers.FormatSize(data.LohTotal)}  ({Pct(data.LohTotal, totalHeap):F1}%)"),
            ("Pinned Object Heap",  DumpHelpers.FormatSize(data.PohTotal)),
            ("Total live objects",  data.TotalObjects.ToString("N0")),
            ("Unique types",        data.TotalUniqueTypes > 0 ? data.TotalUniqueTypes.ToString("N0") : data.AllTypes.Count.ToString("N0")),
        ]);

        if (gen2Pct > 60 && data.Gen2Total > 20_000_000)
            sink.Alert(AlertLevel.Warning,
                $"Gen2 holds {gen2Pct:F0}% of the managed heap \u2014 a growing Gen2 is the primary sign of a managed memory leak.",
                advice: "Take a second dump after a few minutes and run 'trend-analysis <dump1> <dump2>' to confirm growth.");
        if (data.LohTotal > 50_000_000)
            sink.Alert(AlertLevel.Critical,
                $"Large Object Heap is {DumpHelpers.FormatSize(data.LohTotal)} \u2014 LOH is never compacted by default.",
                advice: "Use ArrayPool<T> / MemoryPool<T> for large temporary buffers.");

        sink.Section($"Step 1  \u2014  dumpheap -stat  (top {top} types by total size)");
        sink.Alert(AlertLevel.Info,
            "All managed types sorted by total retained size.",
            detail: "Scan for application types (not System.*) with unexpectedly high Count or TotalSize. " +
                    "Objects accumulating across GC cycles \u2014 especially in Gen2 or LOH \u2014 are the primary leak signal.");

        sink.Table(
            ["Method Table", "Count", "Total Size", "Class Name"],
            data.AllTypes.Take(top).Select(r => new[]
            {
                r.MethodTable > 0 ? $"0x{r.MethodTable:X16}" : "—",
                r.Count.ToString("N0"),
                DumpHelpers.FormatSize(r.Size),
                r.Name.Length > 72 ? "\u2026" + r.Name[^71..] : r.Name,
            }).ToList(),
            $"Total: {data.TotalObjects:N0} objects  |  {(data.TotalUniqueTypes > 0 ? data.TotalUniqueTypes : data.AllTypes.Count):N0} unique types");
    }

    private static void RenderFindings(IRenderSink sink, MemoryLeakData data)
    {
        bool any = false;
        long totalHeap = data.TotalHeapSize;
        static double Pct(long part, long total) => total > 0 ? part * 100.0 / total : 0;

        foreach (var s in data.CountSuspects.Where(t => t.Count >= 10_000).Take(3))
        {
            any = true;
            bool survivedGen2 = s.Gen2Size > s.Size * 0.4;
            string typeTrunc = s.Name.Length > 55 ? "\u2026" + s.Name[^54..] : s.Name;
            sink.Alert(
                survivedGen2 ? AlertLevel.Critical : AlertLevel.Warning,
                $"[High Instance Count] {s.Count:N0} \u00d7 '{typeTrunc}'",
                detail: $"Total size: {DumpHelpers.FormatSize(s.Size)}   " +
                        $"Gen2: {s.Gen2Count:N0} ({DumpHelpers.FormatSize(s.Gen2Size)})   " +
                        $"LOH: {s.LohCount:N0}",
                advice: survivedGen2
                    ? "Objects surviving into Gen2 are not being collected. " +
                      "A static cache, global list, or long-lived singleton is retaining them. " +
                      "Step 4 (gcroot chains) will identify the exact retaining root."
                    : "High count but mostly in Gen0/1 \u2014 may be churn rather than a leak. " +
                      "Take a second dump and compare with 'trend-analysis <dump1> <dump2>'.");
        }

        foreach (var s in data.SizeSuspects.Take(5))
        {
            any = true;
            long avg    = s.Count > 0 ? s.Size / s.Count : 0;
            bool isLoh  = s.LohSize  > s.Size * 0.5;
            bool isGen2 = s.Gen2Size > s.Size * 0.3;
            var  level  = (s.Size >= 50_000_000 || isLoh) ? AlertLevel.Critical : AlertLevel.Warning;
            string typeTrunc = s.Name.Length > 55 ? "\u2026" + s.Name[^54..] : s.Name;
            sink.Alert(level,
                $"[Large Retained Size] '{typeTrunc}'  \u2014  {DumpHelpers.FormatSize(s.Size)}  across {s.Count:N0} instance(s)",
                detail: $"Average per instance: {DumpHelpers.FormatSize(avg)}   " +
                        $"LOH: {DumpHelpers.FormatSize(s.LohSize)}   Gen2: {DumpHelpers.FormatSize(s.Gen2Size)}",
                advice: isLoh
                    ? "Large objects on the LOH are never compacted. Pool or reuse them with ArrayPool<T> / MemoryPool<T>. " +
                      "Step 4 will trace which root is keeping this allocation alive."
                    : isGen2
                        ? "Large objects surviving in Gen2 indicate a long-lived or unpooled allocation. " +
                          "Step 4 (gcroot chains) will trace the retaining root."
                        : "Large retained size detected. Monitor across two dumps with 'trend-analysis' to confirm whether it is growing.");
        }

        double gen2Pct = Pct(data.Gen2Total, totalHeap);
        if (gen2Pct > 60 && data.Gen2Total > 20_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Warning,
                $"[Gen2 Dominance] Gen2 holds {gen2Pct:F0}% of the heap ({DumpHelpers.FormatSize(data.Gen2Total)})",
                detail: "A healthy heap has most live data in Gen0/1. " +
                        "Gen2 dominating means objects are surviving multiple GC cycles.",
                advice: "Capture a second dump and run 'trend-analysis <dump1> <dump2> --full' to confirm the growth trend.");
        }

        if (data.LohTotal > 50_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Critical,
                $"[LOH Pressure] Large Object Heap is {DumpHelpers.FormatSize(data.LohTotal)}",
                detail: "Objects > 85 KB go directly to the LOH which is never compacted by default. " +
                        "Repeated allocation and release of large objects fragments the LOH permanently.",
                advice: "Use ArrayPool<T> / MemoryPool<T> for large temporary buffers. " +
                        "Force compaction once with: GCSettings.LargeObjectHeapCompactionMode = " +
                        "GCLargeObjectHeapCompactionMode.CompactOnce;");
        }
        else if (data.LohTotal > 10_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Warning,
                $"[LOH Pressure] Large Object Heap is {DumpHelpers.FormatSize(data.LohTotal)}",
                advice: "Pool or reuse large arrays to keep the LOH stable over time.");
        }

        if (!any)
            sink.Alert(AlertLevel.Info,
                "No strong memory leak signals detected in this single snapshot.",
                detail: "Neither a high object count (count-based) nor a large retained type (size-based) was found above threshold.",
                advice: "To confirm or rule out a slow leak, capture two dumps at different times and run:\n" +
                        "  trend-analysis <dump1> <dump2> --full");
    }

    private static void RenderCountSuspects(IRenderSink sink, IReadOnlyList<SuspectRow> suspects,
        int minCount, bool includeSystem)
    {
        sink.Alert(AlertLevel.Info,
            "2a  High Instance Count  (count-based leak signal)",
            detail: includeSystem
                ? "All types shown (--include-system). Sorted by instance count descending."
                : "Non-system types with the highest instance count. " +
                  "A growing count across GC cycles is the classic managed-memory-leak pattern. " +
                  "Use --include-system to also show framework types.");

        if (suspects.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                $"No types with \u2265 {minCount:N0} instances or \u2265 1 MB total size found.",
                advice: "Lower the threshold with --min-count (e.g. --min-count 100), or add --include-system.");
        }
        else
        {
            sink.Table(
                ["Type", "Count \u2193", "Total Size", "in Gen2", "in LOH"],
                suspects.Select(t => new[]
                {
                    t.Name.Length > 65 ? t.Name[..65] + "\u2026" : t.Name,
                    t.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.Size),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "\u2014",
                    t.LohCount  > 0 ? $"{t.LohCount:N0}  ({DumpHelpers.FormatSize(t.LohSize)})"  : "\u2014",
                }).ToList(),
                "High count + Gen2 presence = not being collected = strong managed memory leak signal.");
        }
    }

    private static void RenderSizeSuspects(IRenderSink sink, IReadOnlyList<SuspectRow> sizeSuspects)
    {
        const long SizeSuspectMinBytes = 10_485_760;

        sink.Alert(AlertLevel.Info,
            "2b  Large Retained Size  (size-based leak signal)",
            detail: "Types dominating the heap by TOTAL SIZE regardless of instance count. " +
                    "A small number of very large objects (arrays, buffers, caches) can consume hundreds of MB " +
                    "while never appearing in a count-based suspect list. Includes all types \u2014 system and application.");

        if (sizeSuspects.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                $"No additional types with \u2265 {DumpHelpers.FormatSize(SizeSuspectMinBytes)} total size found.");
        }
        else
        {
            sink.Table(
                ["Type", "Total Size \u2193", "Count", "Avg / Instance", "in Gen2", "in LOH"],
                sizeSuspects.Select(t =>
                {
                    long avg = t.Count > 0 ? t.Size / t.Count : 0;
                    return new[]
                    {
                        t.Name.Length > 60 ? t.Name[..60] + "\u2026" : t.Name,
                        DumpHelpers.FormatSize(t.Size),
                        t.Count.ToString("N0"),
                        DumpHelpers.FormatSize(avg),
                        t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "\u2014",
                        t.LohCount  > 0 ? $"{t.LohCount:N0}  ({DumpHelpers.FormatSize(t.LohSize)})"  : "\u2014",
                    };
                }).ToList(),
                "Few instances with huge average size = large-array or cache accumulation. LOH = never compacted by GC.");
        }
    }

    private static void RenderAccumulationPatterns(IRenderSink sink, MemoryLeakData data)
    {
        var p = data.Patterns;

        string StrStatus()  => p.StringCount == 0 ? "—" : p.StringCount > 50_000 ? "⚠ High" : p.StringCount > 10_000 ? "~ Elevated" : "✓ Normal";
        string ByteStatus() => p.ByteArrCount == 0 ? "—" : p.ByteArrLohSize > 10_000_000 ? "⚠ LOH pressure" : p.ByteArrCount > 5_000 ? "⚠ High count" : "✓ Normal";
        string CollStatus() => p.CollTotalSize > 5_000_000 ? "⚠ High" : p.TopCollections.Count == 0 ? "— None" : "✓ Normal";
        string DelStatus()  => p.DelegateCount > 1_000 ? "⚠ High" : "✓ Normal";
        string TaskStatus() => p.TaskCount > 500 ? "⚠ High" : "✓ Normal";

        sink.Table(
            ["Pattern", "Key Types", "Count", "Total Size", "Status"],
            [
                ["Strings",          "System.String",
                    p.StringCount.ToString("N0"),
                    DumpHelpers.FormatSize(p.StringSize),
                    StrStatus()],
                ["Byte arrays",      "System.Byte[]",
                    p.ByteArrCount.ToString("N0"),
                    DumpHelpers.FormatSize(p.ByteArrSize),
                    ByteStatus()],
                ["Collections",      "List<T> / Dictionary / HashSet",
                    p.TopCollections.Sum(r => r.Count).ToString("N0"),
                    DumpHelpers.FormatSize(p.CollTotalSize),
                    CollStatus()],
                ["Delegates/Events", "Action / Func / EventHandler",
                    p.DelegateCount.ToString("N0"),
                    DumpHelpers.FormatSize(p.DelegateSize),
                    DelStatus()],
                ["Tasks/Async",      "Task / async state machines",
                    p.TaskCount.ToString("N0"),
                    DumpHelpers.FormatSize(p.TaskSize),
                    TaskStatus()],
            ],
            "A flag here doesn't guarantee a leak — use 'trend-analysis' across two dumps to confirm growth.");

        bool anyPatternFlag = false;

        // 3a: Strings
        if (p.StringCount > 10_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Strings  —  {p.StringCount:N0} instances  /  {DumpHelpers.FormatSize(p.StringSize)}  (in Gen2: {DumpHelpers.FormatSize(p.StringGen2Size)})", open: true);
            sink.Alert(p.StringCount > 50_000 ? AlertLevel.Warning : AlertLevel.Info,
                $"{p.StringCount:N0} System.String instances ({DumpHelpers.FormatSize(p.StringSize)})",
                detail: "Strings accumulate when parent objects (caches, event subscriptions) are never released. " +
                        "Strings are the SYMPTOM — the root cause is the object graph holding them.",
                advice: "Step 4 will trace the retention chain. Or: gc-roots <dump> --type String --max-results 3");
            sink.EndDetails();
        }

        // 3b: Byte arrays
        if (p.ByteArrLohSize > 10_000_000 || p.ByteArrCount > 5_000)
        {
            anyPatternFlag = true;
            long avg = p.ByteArrCount > 0 ? p.ByteArrSize / p.ByteArrCount : 0;
            sink.BeginDetails($"⚠ Byte arrays  —  {p.ByteArrCount:N0} instances  /  {DumpHelpers.FormatSize(p.ByteArrSize)}" +
                (p.ByteArrLohSize > 0 ? $"  (LOH: {DumpHelpers.FormatSize(p.ByteArrLohSize)})" : ""), open: true);
            if (p.ByteArrLohSize > 10_000_000)
                sink.Alert(AlertLevel.Warning,
                    $"{p.ByteArrLohCount:N0} large Byte[] on LOH totalling {DumpHelpers.FormatSize(p.ByteArrLohSize)}",
                    detail: "LOH byte arrays are never compacted. Repeated alloc/free of large buffers causes fragmentation.",
                    advice: "Use ArrayPool<byte>.Shared to rent and return buffers instead of allocating new arrays.");
            else
                sink.Alert(AlertLevel.Warning,
                    $"{p.ByteArrCount:N0} Byte[] instances  (avg {DumpHelpers.FormatSize(avg)})",
                    advice: "High count can indicate HTTP body, stream, or socket buffers not being pooled. Use MemoryPool<byte>.");
            sink.EndDetails();
        }

        // 3c: Collections
        if (p.CollTotalSize > 5_000_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Collections  —  {DumpHelpers.FormatSize(p.CollTotalSize)} combined", open: true);
            sink.Table(
                ["Collection Type", "Count", "Total Size", "in Gen2"],
                p.TopCollections.Select(t => new[] {
                    t.Name.Length > 65 ? t.Name[..65] + "\u2026" : t.Name,
                    t.Count.ToString("N0"), DumpHelpers.FormatSize(t.Size),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "—" }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"Collections hold {DumpHelpers.FormatSize(p.CollTotalSize)} — check for unbounded caches or static dictionaries.",
                advice: "Set capacity limits, use weak-reference caches (ConditionalWeakTable), or add eviction policies.");
            sink.EndDetails();
        }

        // 3d: Delegates
        if (p.DelegateCount > 1_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Delegates  —  {p.DelegateCount:N0} instances", open: true);
            sink.Table(
                ["Delegate Type", "Count", "Total Size"],
                p.TopDelegates.Select(t => new[] {
                    t.Name.Length > 65 ? t.Name[..65] : t.Name,
                    t.Count.ToString("N0"), DumpHelpers.FormatSize(t.Size) }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"{p.DelegateCount:N0} delegate instances — possible event handler leak.",
                detail: "Event handlers keep both the publisher and subscriber alive. " +
                        "A short-lived object subscribed to a long-lived event cannot be collected.",
                advice: "Unsubscribe with -= in Dispose(), or use weak-event patterns. " +
                        "Run 'event-analysis <dump>' for a detailed breakdown.");
            sink.EndDetails();
        }

        // 3e: Tasks
        if (p.TaskCount > 500)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Tasks / Async  —  {p.TaskCount:N0} instances", open: true);
            sink.Table(
                ["Task / State Machine Type", "Count", "Total Size", "in Gen2"],
                p.TopTasks.Select(t => new[] {
                    t.Name.Length > 65 ? t.Name[..65] + "\u2026" : t.Name,
                    t.Count.ToString("N0"), DumpHelpers.FormatSize(t.Size),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "—" }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"{p.TaskCount:N0} Task / async state machine instances — check for abandoned tasks.",
                detail: "Tasks that are never awaited hold all captured closure variables alive.",
                advice: "Ensure every Task is awaited or disposed. " +
                        "Add top-level exception handling to prevent fire-and-forget task state from accumulating.");
            sink.EndDetails();
        }

        if (!anyPatternFlag)
            sink.Alert(AlertLevel.Info, "All accumulation pattern checks passed — no flags raised.");
    }

    private static void RenderRootChains(IRenderSink sink, MemoryLeakData data)
    {
        foreach (var rc in data.RootChains)
        {
            bool open = rc == data.RootChains[0];
            sink.BeginDetails(
                $"gcroot \u2192 {(rc.TypeName.Length > 65 ? rc.TypeName[..65] + "\u2026" : rc.TypeName)}  " +
                $"({rc.Count:N0} instances  /  {DumpHelpers.FormatSize(rc.TotalSize)})",
                open: open);

            int shown = Math.Min(rc.SampleChains.Count, 3);
            sink.Text($"  Showing root chain for {shown} of {rc.Count:N0} instance(s):");
            sink.BlankLine();

            foreach (var sc in rc.SampleChains.Take(3))
            {
                sink.Text($"  \u250c\u2500 Instance  0x{sc.Addr:X16}  ({DumpHelpers.FormatSize(sc.OwnSize)})");

                if (sc.Chain.Count == 0)
                    sink.Text("  \u2514\u25ba (object is itself a direct GC root)");
                else
                    foreach (var step in sc.Chain)
                        sink.Text(step.IsRoot ? $"  \u2514\u25ba ROOT  {step.Line}" : $"  \u2502   \u2192 {step.Line}");

                sink.BlankLine();
            }

            sink.EndDetails();
        }
    }
}
