using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

/// <summary>
/// All-in-one memory leak detector.
/// Four-step workflow:
///   Step 1  dumpheap -stat      → all types ranked by total size
///   Step 2  Application suspects → your types with high counts
///   Step 3  String check         → System.String accumulation (common symptom, not cause)
///   Step 4  gcroot chains        → which static field / GC handle retains each suspect
/// </summary>
internal static class MemoryLeakCommand
{
    private const string Help = """
        Usage: DumpDetective memory-leak <dump-file> [options]

        Four-step leak detection workflow:

          Step 1 — dumpheap -stat
            All types ranked by total size. Look for unexpectedly large Count or TotalSize.

          Step 2 — Application type suspects
            Your types (non-System.*) with the highest instance count.

          Step 3 — String accumulation check
            System.String is a common symptom — strings accumulate when parent objects
            are held in a cache or collection that is never evicted.

          Step 4 — GC root chains  (gcroot simulation)
            Traces which static field, GC handle, or thread stack keeps each suspect alive.
            Example chain: HandleTable → Object[] → Cache → List<T> → T[] → T → String.

        Options:
          -n, --top <N>        Top N types in the dumpheap-stat table (default: 30)
          --min-count <N>      Min instances to appear in suspect table (default: 500)
          --no-root-trace      Skip Step 4 GC root tracing (faster for a quick overview)
          --include-system     Include System.* / Microsoft.* in the suspect table
          -o, --output <file>  Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective memory-leak app.dmp
          DumpDetective memory-leak app.dmp --output leak.html
          DumpDetective memory-leak app.dmp --no-root-trace
          DumpDetective memory-leak app.dmp --min-count 100 --top 50 --include-system --output leak.html
        """;

    // ── Mutable accumulator used during the heap walk ─────────────────────────
    private sealed class TypeAccum
    {
        public long  Cnt, Sz;
        public long  G0c, G0s, G1c, G1s, G2c, G2s, Lc, Ls;
        public ulong MT;
        public readonly List<ulong> SampleAddrs = [];
    }

    // ── Immutable snapshot after heap walk ────────────────────────────────────
    private sealed record TypeStat(
        string Name, ulong MT,
        long Count, long TotalSize,
        long Gen0Count, long Gen0Size,
        long Gen1Count, long Gen1Size,
        long Gen2Count, long Gen2Size,
        long LohCount, long LohSize,
        IReadOnlyList<ulong> SampleAddrs);

    // ── Entry point ───────────────────────────────────────────────────────────
    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int  top         = 30;
        int  minCount    = 500;
        bool noRootTrace = false;
        bool inclSystem  = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if      ((args[i] is "--top" or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if (args[i] == "--min-count"     && i + 1 < args.Length) int.TryParse(args[++i], out minCount);
            else if (args[i] == "--no-root-trace") noRootTrace = true;
            else if (args[i] == "--include-system") inclSystem = true;
        }

        return CommandBase.Execute(dumpPath, output,
            (ctx, sink) => Render(ctx, sink, top, minCount, noRootTrace, inclSystem));
    }

    // ── Main renderer ──────────────────────────────────────────────────────────
    internal static void Render(DumpContext ctx, IRenderSink sink,
        int top = 30, int minCount = 500, bool noRootTrace = false, bool inclSystem = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Memory Leak Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        sink.Reference(
            "For a detailed walkthrough of how this analysis works, see the Microsoft documentation",
            "https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-memory-leak");

        if (!ctx.Heap.CanWalkHeap)
        {
            sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete.");
            return;
        }

        // Step 1 — dumpheap-stat equivalent (single heap walk)
        var (allTypes, gen0Total, gen1Total, gen2Total, lohTotal, pohTotal, totalObjs, typeMap) =
            WalkHeap(ctx);
        long totalHeap = allTypes.Sum(t => t.TotalSize);

        sink.Section("Heap Snapshot");
        RenderHeapSnapshot(sink, allTypes, gen0Total, gen1Total, gen2Total, lohTotal, pohTotal,
            totalObjs, typeMap, top, totalHeap);

        // Step 2 — Suspect types (count-based AND size-based)
        var (suspects, sizeSuspects) = ComputeSuspects(allTypes, minCount, inclSystem);

        sink.Section("Step 2  —  Suspect Types");
        RenderSuspects(sink, suspects, sizeSuspects, minCount, inclSystem);

        // Step 3 — Common accumulation pattern checks
        sink.Section("Step 3  —  Accumulation Pattern Checks");
        RenderAccumulationPatterns(sink, allTypes);

        // ── Findings summary ───────────────────────────────────────────────────
        sink.Section("Findings");
        EmitFindings(sink, suspects, sizeSuspects, gen2Total, lohTotal, totalHeap);

        // ══════════════════════════════════════════════════════════════════════════
        //  STEP 4 ─ GC root chains  (gcroot simulation)
        // ══════════════════════════════════════════════════════════════════════════
        sink.Section("Step 4  —  GC Root Chains  (gcroot simulation)");
        // Reserve up to 3 slots for count-based suspects, up to 2 for size-based,
        // so a large-array suspect like System.Int64[] is never squeezed out.
        var countCandidates = suspects.Take(3).ToList();
        var sizeCandidates  = sizeSuspects
            .Where(s => !countCandidates.Any(x => x.Name == s.Name))
            .Take(2)
            .ToList();
        var rootCandidates  = countCandidates.Concat(sizeCandidates).ToList();

        if (noRootTrace)
        {
            sink.Alert(AlertLevel.Info,
                "Root tracing skipped (--no-root-trace).",
                detail: "This is the most important diagnostic step — it reveals which static field, " +
                        "GC handle, or thread stack is keeping your suspect objects alive, " +
                        "just like 'gcroot <addr>' in dotnet-dump.",
                advice: "Re-run without --no-root-trace to get full root chains. " +
                        "For a single type: DumpDetective gc-roots <dump> --type \"<TypeName>\"");
        }
        else if (rootCandidates.Count == 0)
        {
            sink.Alert(AlertLevel.Info,
                "No suspect types to trace — nothing above the Step 2 threshold.",
                advice: "Lower --min-count to surface more candidates, or use --include-system.");
        }
        else
        {
            sink.Alert(AlertLevel.Info,
                $"Tracing root chains for top {rootCandidates.Count} suspect type(s).",
                detail: "The chain reads bottom-up: the last entry is the ROOT preventing GC collection. " +
                        "A typical chain looks like: HandleTable → Object[] → Cache → List<T> → T[] → T. " +
                        "The root type (static field, GC handle, or thread local) is where you need to act.");

            RenderAllRootChains(ctx, sink, rootCandidates);
        }

        // ── Next steps ─────────────────────────────────────────────────────────
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

    // Renders the Step 1 heap-snapshot KV block, gen2/LOH alerts, and the dumpheap-stat table.
    private static void RenderHeapSnapshot(
        IRenderSink sink,
        List<TypeStat> allTypes,
        long gen0Total, long gen1Total, long gen2Total, long lohTotal, long pohTotal,
        int totalObjs, Dictionary<string, TypeAccum> typeMap,
        int top, long totalHeap)
    {
        double gen2Pct = Pct(gen2Total, totalHeap);
        sink.KeyValues([
            ("Total managed heap",  DumpHelpers.FormatSize(totalHeap)),
            ("Generation 0",        $"{DumpHelpers.FormatSize(gen0Total)}  ({Pct(gen0Total, totalHeap):F1}%)"),
            ("Generation 1",        $"{DumpHelpers.FormatSize(gen1Total)}  ({Pct(gen1Total, totalHeap):F1}%)"),
            ("Generation 2",        $"{DumpHelpers.FormatSize(gen2Total)}  ({gen2Pct:F1}%  of heap)"),
            ("Large Object Heap",   $"{DumpHelpers.FormatSize(lohTotal)}  ({Pct(lohTotal, totalHeap):F1}%)"),
            ("Pinned Object Heap",  DumpHelpers.FormatSize(pohTotal)),
            ("Total live objects",  totalObjs.ToString("N0")),
            ("Unique types",        typeMap.Count.ToString("N0")),
        ]);

        if (gen2Pct > 60 && gen2Total > 20_000_000)
            sink.Alert(AlertLevel.Warning,
                $"Gen2 holds {gen2Pct:F0}% of the managed heap \u2014 a growing Gen2 is the primary sign of a managed memory leak.",
                advice: "Take a second dump after a few minutes and run 'trend-analysis <dump1> <dump2>' to confirm growth.");
        if (lohTotal > 50_000_000)
            sink.Alert(AlertLevel.Critical,
                $"Large Object Heap is {DumpHelpers.FormatSize(lohTotal)} \u2014 LOH is never compacted by default.",
                advice: "Use ArrayPool<T> / MemoryPool<T> for large temporary buffers.");

        sink.Section($"Step 1  \u2014  dumpheap -stat  (top {top} types by total size)");
        sink.Alert(AlertLevel.Info,
            "All managed types sorted by total retained size.",
            detail: "Scan for application types (not System.*) with unexpectedly high Count or TotalSize. " +
                    "Objects accumulating across GC cycles \u2014 especially in Gen2 or LOH \u2014 are the primary leak signal.");

        sink.Table(
            ["Method Table", "Count", "Total Size", "Class Name"],
            allTypes.Take(top).Select(t => new[]
            {
                $"0x{t.MT:X16}",
                t.Count.ToString("N0"),
                DumpHelpers.FormatSize(t.TotalSize),
                Truncate(t.Name, 72),
            }).ToList(),
            $"Total: {totalObjs:N0} objects  |  {typeMap.Count:N0} unique types");
    }

    // Partitions allTypes into count-based and size-based suspect lists.
    // Count-based: non-system (or all when inclSystem=true) types exceeding minCount instances or 1 MB.
    // Size-based: any type ≥ 10 MB that is not already in the count-based list.
    private static (List<TypeStat> Suspects, List<TypeStat> SizeSuspects)
        ComputeSuspects(List<TypeStat> allTypes, int minCount, bool inclSystem)
    {
        var suspects = allTypes
            .Where(t => inclSystem || !DumpHelpers.IsSystemType(t.Name))
            .Where(t => t.Count >= minCount || t.TotalSize >= 1_048_576)
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => t.TotalSize)
            .Take(20)
            .ToList();

        const long SizeSuspectMinBytes = 10_485_760; // 10 MB
        var sizeSuspects = allTypes
            .Where(t => t.TotalSize >= SizeSuspectMinBytes)
            .Where(t => t.Count > 0)
            .Where(t => !suspects.Any(s => s.Name == t.Name))
            .OrderByDescending(t => t.TotalSize)
            .Take(10)
            .ToList();

        return (suspects, sizeSuspects);
    }

    // Renders the Step 2 suspect tables: 2a count-based and 2b size-based.
    private static void RenderSuspects(
        IRenderSink sink,
        List<TypeStat> suspects,
        List<TypeStat> sizeSuspects,
        int minCount,
        bool inclSystem)
    {
        const long SizeSuspectMinBytes = 10_485_760;

        sink.Alert(AlertLevel.Info,
            "2a  High Instance Count  (count-based leak signal)",
            detail: inclSystem
                ? "All types shown (--include-system). Sorted by instance count descending."
                : "Non-system types with the highest instance count. " +
                  "A growing count across GC cycles is the classic managed-memory-leak pattern. " +
                  "Use --include-system to also show framework types.");

        if (suspects.Count == 0)
            sink.Alert(AlertLevel.Info,
                $"No types with \u2265 {minCount:N0} instances or \u2265 1 MB total size found.",
                advice: "Lower the threshold with --min-count (e.g. --min-count 100), or add --include-system.");
        else
            sink.Table(
                ["Type", "Count \u2193", "Total Size", "in Gen2", "in LOH"],
                suspects.Select(t => new[]
                {
                    Truncate(t.Name, 65),
                    t.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.TotalSize),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "\u2014",
                    t.LohCount  > 0 ? $"{t.LohCount:N0}  ({DumpHelpers.FormatSize(t.LohSize)})"  : "\u2014",
                }).ToList(),
                "High count + Gen2 presence = not being collected = strong managed memory leak signal.");

        sink.Alert(AlertLevel.Info,
            "2b  Large Retained Size  (size-based leak signal)",
            detail: "Types dominating the heap by TOTAL SIZE regardless of instance count. " +
                    "A small number of very large objects (arrays, buffers, caches) can consume hundreds of MB " +
                    "while never appearing in a count-based suspect list. Includes all types \u2014 system and application.");

        if (sizeSuspects.Count == 0)
            sink.Alert(AlertLevel.Info,
                $"No additional types with \u2265 {DumpHelpers.FormatSize(SizeSuspectMinBytes)} total size found.");
        else
            sink.Table(
                ["Type", "Total Size \u2193", "Count", "Avg / Instance", "in Gen2", "in LOH"],
                sizeSuspects.Select(t =>
                {
                    long avg = t.Count > 0 ? t.TotalSize / t.Count : 0;
                    return new[]
                    {
                        Truncate(t.Name, 60),
                        DumpHelpers.FormatSize(t.TotalSize),
                        t.Count.ToString("N0"),
                        DumpHelpers.FormatSize(avg),
                        t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "\u2014",
                        t.LohCount  > 0 ? $"{t.LohCount:N0}  ({DumpHelpers.FormatSize(t.LohSize)})"  : "\u2014",
                    };
                }).ToList(),
                "Few instances with huge average size = large-array or cache accumulation. LOH = never compacted by GC.");
    }

    // Walks the heap once accumulating per-type counts, sizes, and generation breakdowns (Step 1).
    // Returns the sorted allTypes list, generation totals, object count, and the raw accumulator map.
    private static (List<TypeStat> AllTypes,
                    long Gen0Total, long Gen1Total, long Gen2Total, long LohTotal, long PohTotal,
                    int TotalObjs, Dictionary<string, TypeAccum> TypeMap)
        WalkHeap(DumpContext ctx)
    {
        var typeMap = new Dictionary<string, TypeAccum>(StringComparer.Ordinal);
        long gen0Total = 0, gen1Total = 0, gen2Total = 0, lohTotal = 0, pohTotal = 0;
        int  totalObjs = 0;

        var sw = Stopwatch.StartNew();
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start("Step 1 / 4 — Walking heap (dumpheap -stat)…", status =>
            {
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;

                    string name = obj.Type.Name ?? "<unknown>";
                    long   size = (long)obj.Size;
                    var    seg  = ctx.Heap.GetSegmentByAddress(obj.Address);
                    bool   g0   = seg?.Kind is GCSegmentKind.Generation0;
                    bool   g1   = seg?.Kind is GCSegmentKind.Generation1;
                    bool   g2   = seg?.Kind is GCSegmentKind.Generation2;
                    bool   loh  = seg?.Kind is GCSegmentKind.Large;
                    bool   poh  = seg?.Kind is GCSegmentKind.Pinned;

                    if (!typeMap.TryGetValue(name, out var acc))
                    {
                        acc = new TypeAccum { MT = obj.Type.MethodTable };
                        typeMap[name] = acc;
                    }

                    acc.Cnt += 1;    acc.Sz  += size;
                    if (g0)  { acc.G0c++; acc.G0s += size; }
                    if (g1)  { acc.G1c++; acc.G1s += size; }
                    if (g2)  { acc.G2c++; acc.G2s += size; }
                    if (loh) { acc.Lc++;  acc.Ls  += size; }
                    if (acc.SampleAddrs.Count < 5) acc.SampleAddrs.Add(obj.Address);

                    if (g0)  gen0Total += size;
                    if (g1)  gen1Total += size;
                    if (g2)  gen2Total += size;
                    if (loh) lohTotal  += size;
                    if (poh) pohTotal  += size;
                    totalObjs++;

                    if (sw.Elapsed.TotalSeconds >= 0.75)
                    {
                        status.Status($"Step 1 / 4 — Walking heap — {totalObjs:N0} objects…");
                        sw.Restart();
                    }
                }
            });

        AnsiConsole.MarkupLine(
            $"[dim]  Heap walk complete ({sw.Elapsed.TotalSeconds:F1}s  |  " +
            $"{totalObjs:N0} objects  |  {typeMap.Count:N0} unique types)[/]");

        // Build typed list sorted by TotalSize descending — same ordering as dumpheap -stat
        var allTypes = typeMap
            .Select(kv => new TypeStat(
                kv.Key, kv.Value.MT,
                kv.Value.Cnt, kv.Value.Sz,
                kv.Value.G0c, kv.Value.G0s,
                kv.Value.G1c, kv.Value.G1s,
                kv.Value.G2c, kv.Value.G2s,
                kv.Value.Lc,  kv.Value.Ls,
                kv.Value.SampleAddrs))
            .OrderByDescending(t => t.TotalSize)
            .ToList();

        return (allTypes, gen0Total, gen1Total, gen2Total, lohTotal, pohTotal, totalObjs, typeMap);
    }

    // Renders the Step 3 accumulation-pattern detail blocks for any flagged patterns.
    // Evaluates strings, byte arrays, collections, delegates, and async tasks.
    private static void RenderAccumulationPatterns(IRenderSink sink, List<TypeStat> allTypes)
    {
        // ── Gather data for all 5 patterns ────────────────────────────────────
        var strType = allTypes.FirstOrDefault(t => t.Name == "System.String");
        var byteArr = allTypes.FirstOrDefault(t => t.Name == "System.Byte[]");

        var collections = allTypes
            .Where(t => t.Name != null && (
                t.Name.StartsWith("System.Collections.Generic.List`1",               StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Collections.Generic.Dictionary`2",         StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2", StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Collections.Generic.HashSet`1",            StringComparison.Ordinal)))
            .OrderByDescending(t => t.TotalSize).Take(8).ToList();
        long collTotal = collections.Sum(t => t.TotalSize);

        var delegates = allTypes
            .Where(t => t.Name != null && (
                t.Name.StartsWith("System.EventHandler", StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Action`",      StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Func`",        StringComparison.Ordinal) ||
                t.Name == "System.Action" || t.Name == "System.MulticastDelegate"))
            .OrderByDescending(t => t.Count).Take(6).ToList();
        long delegateCount = delegates.Sum(t => t.Count);

        var tasksAndSm = allTypes
            .Where(t => t.Name != null && (
                t.Name.StartsWith("System.Threading.Tasks.Task",              StringComparison.Ordinal) ||
                t.Name.StartsWith("System.Runtime.CompilerServices.AsyncTask", StringComparison.Ordinal) ||
                (t.Name.Contains("+<", StringComparison.Ordinal) && t.Name.Contains(">d__", StringComparison.Ordinal))))
            .OrderByDescending(t => t.Count).Take(8).ToList();
        long taskCount = tasksAndSm.Sum(t => t.Count);

        // ── Determine per-pattern status ──────────────────────────────────────
        string StrStatus()
        {
            if (strType is null)             return "—";
            if (strType.Count > 50_000)      return "⚠ High";
            if (strType.Count > 10_000)      return "~ Elevated";
            return "✓ Normal";
        }
        string ByteStatus()
        {
            if (byteArr is null)             return "—";
            if (byteArr.LohSize > 10_000_000) return "⚠ LOH pressure";
            if (byteArr.Count > 5_000)       return "⚠ High count";
            return "✓ Normal";
        }
        string CollStatus() => collTotal > 5_000_000 ? "⚠ High" : collections.Count == 0 ? "— None" : "✓ Normal";
        string DelStatus()  => delegateCount > 1_000  ? "⚠ High" : "✓ Normal";
        string TaskStatus() => taskCount > 500         ? "⚠ High" : "✓ Normal";

        // ── Summary table — one row per pattern, at a glance ─────────────────
        sink.Table(
            ["Pattern", "Key Types", "Count", "Total Size", "Status"],
            [
                ["Strings",          "System.String",
                    strType?.Count.ToString("N0") ?? "0",
                    strType is not null ? DumpHelpers.FormatSize(strType.TotalSize) : "0 B",
                    StrStatus()],
                ["Byte arrays",      "System.Byte[]",
                    byteArr?.Count.ToString("N0") ?? "0",
                    byteArr is not null ? DumpHelpers.FormatSize(byteArr.TotalSize) : "0 B",
                    ByteStatus()],
                ["Collections",      "List<T> / Dictionary / HashSet",
                    collections.Sum(t => t.Count).ToString("N0"),
                    DumpHelpers.FormatSize(collTotal),
                    CollStatus()],
                ["Delegates/Events", "Action / Func / EventHandler",
                    delegateCount.ToString("N0"),
                    DumpHelpers.FormatSize(delegates.Sum(t => t.TotalSize)),
                    DelStatus()],
                ["Tasks/Async",      "Task / async state machines",
                    taskCount.ToString("N0"),
                    DumpHelpers.FormatSize(tasksAndSm.Sum(t => t.TotalSize)),
                    TaskStatus()],
            ],
            "A flag here doesn't guarantee a leak — use 'trend-analysis' across two dumps to confirm growth.");

        // ── Detail blocks — only rendered when a pattern flagged ──────────────
        bool anyPatternFlag = false;

        // 3a: Strings
        if (strType is not null && strType.Count > 10_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Strings  —  {strType.Count:N0} instances  /  {DumpHelpers.FormatSize(strType.TotalSize)}  (in Gen2: {DumpHelpers.FormatSize(strType.Gen2Size)})", open: true);
            sink.Alert(strType.Count > 50_000 ? AlertLevel.Warning : AlertLevel.Info,
                $"{strType.Count:N0} System.String instances ({DumpHelpers.FormatSize(strType.TotalSize)})",
                detail: "Strings accumulate when parent objects (caches, event subscriptions) are never released. " +
                        "Strings are the SYMPTOM — the root cause is the object graph holding them.",
                advice: "Step 4 will trace the retention chain. Or: gc-roots <dump> --type String --max-results 3");
            sink.EndDetails();
        }

        // 3b: Byte arrays
        if (byteArr is not null && (byteArr.LohSize > 10_000_000 || byteArr.Count > 5_000))
        {
            anyPatternFlag = true;
            long avgBytes = byteArr.Count > 0 ? byteArr.TotalSize / byteArr.Count : 0;
            sink.BeginDetails($"⚠ Byte arrays  —  {byteArr.Count:N0} instances  /  {DumpHelpers.FormatSize(byteArr.TotalSize)}  (LOH: {DumpHelpers.FormatSize(byteArr.LohSize)})", open: true);
            if (byteArr.LohSize > 10_000_000)
                sink.Alert(AlertLevel.Warning,
                    $"{byteArr.LohCount:N0} large Byte[] on LOH totalling {DumpHelpers.FormatSize(byteArr.LohSize)}",
                    detail: "LOH byte arrays are never compacted. Repeated alloc/free of large buffers causes fragmentation.",
                    advice: "Use ArrayPool<byte>.Shared to rent and return buffers instead of allocating new arrays.");
            else
                sink.Alert(AlertLevel.Warning,
                    $"{byteArr.Count:N0} Byte[] instances  (avg {DumpHelpers.FormatSize(avgBytes)})",
                    advice: "High count can indicate HTTP body, stream, or socket buffers not being pooled. Use MemoryPool<byte>.");
            sink.EndDetails();
        }

        // 3c: Collections
        if (collTotal > 5_000_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Collections  —  {DumpHelpers.FormatSize(collTotal)} combined", open: true);
            sink.Table(
                ["Collection Type", "Count", "Total Size", "in Gen2"],
                collections.Select(t => new[]
                {
                    Truncate(t.Name, 65), t.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.TotalSize),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "—",
                }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"Collections hold {DumpHelpers.FormatSize(collTotal)} — check for unbounded caches or static dictionaries.",
                advice: "Set capacity limits, use weak-reference caches (ConditionalWeakTable), or add eviction policies.");
            sink.EndDetails();
        }

        // 3d: Delegates
        if (delegateCount > 1_000)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Delegates  —  {delegateCount:N0} instances", open: true);
            sink.Table(
                ["Delegate Type", "Count", "Total Size"],
                delegates.Select(t => new[]
                {
                    Truncate(t.Name, 65), t.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.TotalSize),
                }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"{delegateCount:N0} delegate instances — possible event handler leak.",
                detail: "Event handlers keep both the publisher and subscriber alive. " +
                        "A short-lived object subscribed to a long-lived event cannot be collected.",
                advice: "Unsubscribe with -= in Dispose(), or use weak-event patterns. " +
                        "Run 'event-analysis <dump>' for a detailed breakdown.");
            sink.EndDetails();
        }

        // 3e: Tasks
        if (taskCount > 500)
        {
            anyPatternFlag = true;
            sink.BeginDetails($"⚠ Tasks / Async  —  {taskCount:N0} instances", open: true);
            sink.Table(
                ["Task / State Machine Type", "Count", "Total Size", "in Gen2"],
                tasksAndSm.Select(t => new[]
                {
                    Truncate(t.Name, 65), t.Count.ToString("N0"),
                    DumpHelpers.FormatSize(t.TotalSize),
                    t.Gen2Count > 0 ? $"{t.Gen2Count:N0}  ({DumpHelpers.FormatSize(t.Gen2Size)})" : "—",
                }).ToList());
            sink.Alert(AlertLevel.Warning,
                $"{taskCount:N0} Task / async state machine instances — check for abandoned tasks.",
                detail: "Tasks that are never awaited hold all captured closure variables alive.",
                advice: "Ensure every Task is awaited or disposed. " +
                        "Add top-level exception handling to prevent fire-and-forget task state from accumulating.");
            sink.EndDetails();
        }

        if (!anyPatternFlag)
            sink.Alert(AlertLevel.Info, "All accumulation pattern checks passed — no flags raised.");
    }

    // ── Findings emitter ───────────────────────────────────────────────────────
    private static void EmitFindings(IRenderSink sink,
        List<TypeStat> suspects, List<TypeStat> sizeSuspects,
        long gen2Total, long lohTotal, long totalHeap)
    {
        bool any = false;

        // Count-based findings
        foreach (var s in suspects.Where(t => t.Count >= 10_000).Take(3))
        {
            any = true;
            bool survivedGen2 = s.Gen2Size > s.TotalSize * 0.4;
            sink.Alert(
                survivedGen2 ? AlertLevel.Critical : AlertLevel.Warning,
                $"[High Instance Count] {s.Count:N0} × '{Truncate(s.Name, 55)}'",
                detail: $"Total size: {DumpHelpers.FormatSize(s.TotalSize)}   " +
                        $"Gen2: {s.Gen2Count:N0} ({DumpHelpers.FormatSize(s.Gen2Size)})   " +
                        $"LOH: {s.LohCount:N0}",
                advice: survivedGen2
                    ? "Objects surviving into Gen2 are not being collected. " +
                      "A static cache, global list, or long-lived singleton is retaining them. " +
                      "Step 4 (gcroot chains) will identify the exact retaining root."
                    : "High count but mostly in Gen0/1 — may be churn rather than a leak. " +
                      "Take a second dump and compare with 'trend-analysis <dump1> <dump2>'.");
        }

        // Size-based findings — catches large arrays / buffers regardless of instance count
        foreach (var s in sizeSuspects.Take(5))
        {
            any = true;
            long avg    = s.Count > 0 ? s.TotalSize / s.Count : 0;
            bool isLoh  = s.LohSize  > s.TotalSize * 0.5;
            bool isGen2 = s.Gen2Size > s.TotalSize * 0.3;
            var  level  = (s.TotalSize >= 50_000_000 || isLoh) ? AlertLevel.Critical : AlertLevel.Warning;
            sink.Alert(level,
                $"[Large Retained Size] '{Truncate(s.Name, 55)}'  —  {DumpHelpers.FormatSize(s.TotalSize)}  across {s.Count:N0} instance(s)",
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

        double gen2Pct = Pct(gen2Total, totalHeap);
        if (gen2Pct > 60 && gen2Total > 20_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Warning,
                $"[Gen2 Dominance] Gen2 holds {gen2Pct:F0}% of the heap ({DumpHelpers.FormatSize(gen2Total)})",
                detail: "A healthy heap has most live data in Gen0/1. " +
                        "Gen2 dominating means objects are surviving multiple GC cycles.",
                advice: "Capture a second dump and run 'trend-analysis <dump1> <dump2> --full' to confirm the growth trend.");
        }

        if (lohTotal > 50_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Critical,
                $"[LOH Pressure] Large Object Heap is {DumpHelpers.FormatSize(lohTotal)}",
                detail: "Objects > 85 KB go directly to the LOH which is never compacted by default. " +
                        "Repeated allocation and release of large objects fragments the LOH permanently.",
                advice: "Use ArrayPool<T> / MemoryPool<T> for large temporary buffers. " +
                        "Force compaction once with: GCSettings.LargeObjectHeapCompactionMode = " +
                        "GCLargeObjectHeapCompactionMode.CompactOnce;");
        }
        else if (lohTotal > 10_000_000)
        {
            any = true;
            sink.Alert(AlertLevel.Warning,
                $"[LOH Pressure] Large Object Heap is {DumpHelpers.FormatSize(lohTotal)}",
                advice: "Pool or reuse large arrays to keep the LOH stable over time.");
        }

        if (!any)
            sink.Alert(AlertLevel.Info,
                "No strong memory leak signals detected in this single snapshot.",
                detail: "Neither a high object count (count-based) nor a large retained type (size-based) was found above threshold.",
                advice: "To confirm or rule out a slow leak, capture two dumps at different times and run:\n" +
                        "  trend-analysis <dump1> <dump2> --full");
    }

    // ── GC root chain renderer ─────────────────────────────────────────────────
    private static void RenderAllRootChains(DumpContext ctx, IRenderSink sink, List<TypeStat> suspects)
    {
        var rootMap      = ScanGcRoots(ctx);
        var allReferrers = BuildReferrerMap(ctx);

        // Render one collapsible section per suspect type
        foreach (var suspect in suspects)
        {
            bool open = suspect == suspects[0];
            sink.BeginDetails(
                $"gcroot → {Truncate(suspect.Name, 65)}  " +
                $"({suspect.Count:N0} instances  /  {DumpHelpers.FormatSize(suspect.TotalSize)})",
                open: open);

            if (suspect.SampleAddrs.Count == 0)
            {
                sink.Text("  No sample addresses collected.");
                sink.EndDetails();
                continue;
            }

            int shown = Math.Min(suspect.SampleAddrs.Count, 3);
            sink.Text($"  Showing root chain for {shown} of {suspect.Count:N0} instance(s):");
            sink.BlankLine();

            foreach (var addr in suspect.SampleAddrs.Take(3))
            {
                var instObj = ctx.Heap.GetObject(addr);
                long instSz = instObj.IsValid ? (long)instObj.Size : 0;
                sink.Text($"  ┌─ Instance  0x{addr:X16}  ({DumpHelpers.FormatSize(instSz)})");

                var chain = BuildChainBFS(addr, allReferrers, rootMap, maxDepth: 60);
                if (chain.Count == 0)
                    sink.Text("  └► (object is itself a direct GC root)");
                else
                    foreach (var (line, isRoot) in chain)
                        sink.Text(isRoot ? $"  └► ROOT  {line}" : $"  │   → {line}");

                sink.BlankLine();
            }

            sink.EndDetails();
        }
    }

    // Enumerates all GC roots from the heap and builds an address → (kind, object type) map.
    // Lightweight — no heap object walk required.
    private static Dictionary<ulong, (string Kind, string? ObjType)> ScanGcRoots(DumpContext ctx)
    {
        var rootMap = new Dictionary<ulong, (string Kind, string? ObjType)>();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .Start("Step 4a — Enumerating GC roots…", _ =>
            {
                foreach (var root in ctx.Heap.EnumerateRoots())
                {
                    if (root.Object == 0 || rootMap.ContainsKey(root.Object)) continue;
                    string kind = root.RootKind switch
                    {
                        ClrRootKind.Stack             => "Stack (thread local)",
                        ClrRootKind.StrongHandle      => "GC Handle — Strong",
                        ClrRootKind.PinnedHandle       => "GC Handle — Pinned",
                        ClrRootKind.AsyncPinnedHandle => "GC Handle — Async-Pinned",
                        ClrRootKind.RefCountedHandle  => "GC Handle — RefCount",
                        ClrRootKind.FinalizerQueue    => "Finalizer Queue",
                        _                             => root.RootKind.ToString(),
                    };
                    var obj = ctx.Heap.GetObject(root.Object);
                    rootMap[root.Object] = (kind, obj.IsValid ? obj.Type?.Name : null);
                }
            });
        AnsiConsole.MarkupLine($"[dim]  {rootMap.Count:N0} GC roots indexed[/]");
        return rootMap;
    }

    // Full heap walk that builds a multi-parent inbound-reference map (up to 8 referrers per address).
    // Used by BFS root-chain tracing so alternative paths are explored when one path cycles.
    private static Dictionary<ulong, List<(ulong ParentAddr, string ParentType)>> BuildReferrerMap(
        DumpContext ctx)
    {
        const int MaxParents = 8;
        var allReferrers = new Dictionary<ulong, List<(ulong ParentAddr, string ParentType)>>();
        var sw = Stopwatch.StartNew();
        AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .Start("Step 4b — Building multi-parent referrer map (full heap walk)…", status =>
            {
                long scanned = 0;
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    string pType = obj.Type.Name ?? "?";
                    ulong  pAddr = obj.Address;
                    try
                    {
                        foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                        {
                            if (refAddr == 0 || refAddr == pAddr) continue;
                            if (allReferrers.TryGetValue(refAddr, out var existing))
                            {
                                if (existing.Count < MaxParents)
                                    existing.Add((pAddr, pType));
                            }
                            else
                            {
                                allReferrers[refAddr] = new List<(ulong, string)>(2) { (pAddr, pType) };
                            }
                        }
                    }
                    catch { }
                    scanned++;
                    if (sw.Elapsed.TotalSeconds >= 0.75)
                    {
                        status.Status($"Step 4b — Building referrer map — {scanned:N0} objects scanned…");
                        sw.Restart();
                    }
                }
            });
        AnsiConsole.MarkupLine($"[dim]  Referrer map: {allReferrers.Count:N0} entries ({sw.Elapsed.TotalSeconds:F1}s)[/]");
        return allReferrers;
    }

    /// <summary>
    /// BFS from <paramref name="startAddr"/> upward through the referrer graph,
    /// trying ALL stored parents (up to MaxParents) at each hop so that cycles on
    /// one path don't block us from finding the root via an alternative path.
    /// </summary>
    private static List<(string Line, bool IsRoot)> BuildChainBFS(
        ulong startAddr,
        Dictionary<ulong, List<(ulong ParentAddr, string ParentType)>> allReferrers,
        Dictionary<ulong, (string Kind, string? ObjType)> rootMap,
        int maxDepth)
    {
        // prev[addr] = (came_from, type_of_addr)
        // "came_from" is the address one step CLOSER to startAddr.
        // Populated as we expand the BFS frontier.
        var prev = new Dictionary<ulong, (ulong From, string Type)>();
        prev[startAddr] = (0, "");

        var queue = new Queue<(ulong Addr, int Depth)>();
        queue.Enqueue((startAddr, 0));

        ulong rootAddr = 0;

        while (queue.Count > 0 && rootAddr == 0)
        {
            var (curr, depth) = queue.Dequeue();

            // Is this node itself a GC root? (skip startAddr — it's the suspect)
            if (curr != startAddr && rootMap.ContainsKey(curr))
            {
                rootAddr = curr;
                break;
            }

            if (depth >= maxDepth) continue;

            if (!allReferrers.TryGetValue(curr, out var parents)) continue;

            foreach (var (pAddr, pType) in parents)
            {
                if (prev.ContainsKey(pAddr)) continue;  // already visited
                prev[pAddr] = (curr, pType);             // pType = type of pAddr
                queue.Enqueue((pAddr, depth + 1));
            }
        }

        var chain = new List<(string, bool)>();

        if (rootAddr == 0)
        {
            // No root found — reconstruct longest path found before queue emptied
            // as a best-effort partial chain, ending with a diagnostic note.
            chain.Add(("(no GC root path found — object likely held by a static field, native handle, or circular cluster without direct GC root)", false));
            return chain;
        }

        // Reconstruct the path by walking prev from rootAddr back to startAddr.
        // prev[X].From = node closer to startAddr, so we walk toward startAddr.
        var pathNodes = new List<(ulong Addr, string Type, bool IsRoot)>();
        var cur = rootAddr;
        while (cur != 0)
        {
            bool isRoot = rootMap.ContainsKey(cur);
            string display;
            if (isRoot)
            {
                var ri = rootMap[cur];
                string tp = ri.ObjType is not null ? $"  [{Truncate(ri.ObjType, 55)}]" : string.Empty;
                display = $"{ri.Kind}  @0x{cur:X16}{tp}";
            }
            else
            {
                display = $"{Truncate(prev[cur].Type, 70)}  @0x{cur:X16}";
            }
            pathNodes.Add((cur, display, isRoot));

            if (!prev.TryGetValue(cur, out var p) || p.From == 0) break;
            cur = p.From;
        }

        // pathNodes is root-first; reverse to get leaf-first (suspect → ... → root)
        pathNodes.Reverse();
        // Drop the startAddr node itself (it's already shown as the Instance header)
        foreach (var (addr, display, isRoot) in pathNodes)
        {
            if (addr == startAddr) continue;
            chain.Add((display, isRoot));
        }

        return chain;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private static double Pct(long part, long total) =>
        total > 0 ? part * 100.0 / total : 0;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
