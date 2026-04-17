using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpDetective.Commands;

/// <summary>
/// Identifies "hub" objects — those with the highest number of inbound object references.
/// Uses two heap passes: the first builds raw inbound counts; the second profiles which
/// concrete types are pointing at the top candidates.
/// </summary>
internal static class HighRefsCommand
{
    private const string Help = """
        Usage: DumpDetective high-refs <dump-file> [options]

        Finds objects with the most incoming object references ("hub" objects).
        These are common causes of unintended retention, bloated caches,
        shared-state misuse, and GC card-table pressure.

        Options:
          -n, --top <N>        Top N objects to analyse (default: 30)
          -m, --min-refs <N>   Minimum inbound reference count to include (default: 10)
          -a, --addresses      Show object addresses in the summary table
          -o, --output <f>     Write report to file (.html / .md / .txt / .json)
          -h, --help           Show this help

        Examples:
          DumpDetective high-refs app.dmp
          DumpDetective high-refs app.dmp --top 50 --min-refs 5 --output report.html
        """;

    private sealed record CandidateInfo(
        string                              Type,
        long                                OwnSize,
        long                                RetainedSize,
        string                              Gen,
        int                                 InboundRefs,
        int                                 DistinctSourceTypes,
        IReadOnlyList<(string Type, int Count)> TopSources);

    // Retained ≈ shallow size of the object itself plus every directly-referenced
    // child object (backing arrays, entry tables, value slots, etc.).
    // This gives a meaningful footprint for collections without a full BFS.
    private static long ComputeRetained(ClrHeap heap, ClrObject obj)
    {
        long total = (long)obj.Size;
        try
        {
            int cap = 0;
            foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
            {
                if (refAddr == 0 || refAddr == obj.Address) continue;
                var child = heap.GetObject(refAddr);
                if (child.IsValid) total += (long)child.Size;
                if (++cap >= 2000) break; // guard against pathological fan-out
            }
        }
        catch { }
        return total;
    }

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 30;
        int minRefs = 10;
        bool showAddr = false;
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top"      or "-n") && i + 1 < args.Length) int.TryParse(args[++i], out top);
            else if ((args[i] is "--min-refs" or "-m") && i + 1 < args.Length) int.TryParse(args[++i], out minRefs);
            else if (args[i] is "--addresses" or "-a") showAddr = true;
        }

        top = CommandBase.EffectiveTop(top, output);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, top, minRefs, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
                                int top = 30, int minRefs = 10, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);
        sink.Header(
            "Dump Detective — Highly Referenced Object Analysis",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap)
        {
            sink.Alert(AlertLevel.Warning, "Cannot walk heap — dump may be incomplete.");
            return;
        }

        // Use the snapshot's inbound-count table when available (saves a full heap pass)
        Dictionary<ulong, int> inboundCounts;
        long totalRefs, totalObjs;
        if (ctx.Snapshot is { } snap)
        {
            inboundCounts = snap.InboundCounts;
            totalRefs     = snap.TotalRefs;
            totalObjs     = snap.TotalObjects;
        }
        else
        {
            (inboundCounts, totalRefs, totalObjs) = BuildInboundCounts(ctx);
        }

        // ── Identify top candidates ───────────────────────────────────────────
        var topAddrs = inboundCounts
            .Where(kv => kv.Value >= minRefs)
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (topAddrs.Count == 0)
        {
            sink.Section("Summary");
            sink.Alert(AlertLevel.Info, $"No objects with ≥ {minRefs} inbound references found.");
            sink.KeyValues([
                ("Objects scanned",          totalObjs.ToString("N0")),
                ("Total references traced",  totalRefs.ToString("N0")),
                ("Distinct referenced addrs",inboundCounts.Count.ToString("N0")),
            ]);
            return;
        }

        var referencingTypes = BuildReferencingTypes(ctx, topAddrs);
        var candidates        = MaterializeCandidates(ctx.Heap, topAddrs, inboundCounts, referencingTypes);

        RenderSummaryAndAlerts(sink, candidates, totalObjs, totalRefs, inboundCounts, minRefs);
        RenderMainTable(sink, candidates, showAddr, minRefs);
        RenderDetailAccordions(sink, candidates);
        RenderHubDistribution(sink, candidates);
        RenderGenDistribution(sink, candidates, inboundCounts);
        RenderRefHistogram(sink, inboundCounts);
    }

    // Pass 1: enumerate every reachable object and count how many objects point at each address.
    // Returns (inboundCounts dict, total ref edges traversed, total objects scanned).
    static (Dictionary<ulong, int> InboundCounts, long TotalRefs, long TotalObjs)
        BuildInboundCounts(DumpContext ctx)
    {
        var inboundCounts = new Dictionary<ulong, int>(capacity: 65536);
        long totalRefs = 0, totalObjs = 0;

        var sw1 = Stopwatch.StartNew();
        CommandBase.RunStatus("Pass 1/2 — counting inbound references...", upd =>
        {
            var watch = Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                totalObjs++;

                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(inboundCounts, refAddr, out _);
                    c++;
                    totalRefs++;
                }

                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    upd($"Pass 1/2 — {totalObjs:N0} objects, {totalRefs:N0} refs counted...");
                    watch.Restart();
                }
            }
        });
        if (!CommandBase.SuppressVerbose)
            AnsiConsole.MarkupLine($"[dim]  Pass 1 complete ({sw1.Elapsed.TotalSeconds:F1}s — {totalObjs:N0} objects, {totalRefs:N0} references)[/]");

        return (inboundCounts, totalRefs, totalObjs);
    }

    // Pass 2: for each top-candidate address profile which source types are pointing at it.
    // Pre-populates per-candidate type maps so inline increments via CollectionsMarshal are safe.
    static Dictionary<ulong, Dictionary<string, int>> BuildReferencingTypes(
        DumpContext ctx, HashSet<ulong> topAddrs)
    {
        var referencingTypes = new Dictionary<ulong, Dictionary<string, int>>(topAddrs.Count);
        foreach (var a in topAddrs)
            referencingTypes[a] = new Dictionary<string, int>(32, StringComparer.Ordinal);

        var sw2 = Stopwatch.StartNew();
        CommandBase.RunStatus("Pass 2/2 — profiling referencing types...", upd =>
        {
            var watch = Stopwatch.StartNew();
            long processed = 0;

            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                string srcType = obj.Type.Name ?? "<unknown>";
                processed++;

                foreach (var refAddr in obj.EnumerateReferenceAddresses(carefully: false))
                {
                    if (refAddr == 0) continue;
                    if (!referencingTypes.TryGetValue(refAddr, out var typeMap)) continue;
                    ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(typeMap, srcType, out _);
                    c++;
                }

                if (watch.Elapsed.TotalSeconds >= 1)
                {
                    upd($"Pass 2/2 — {processed:N0} objects analysed...");
                    watch.Restart();
                }
            }
        });
        if (!CommandBase.SuppressVerbose)
            AnsiConsole.MarkupLine($"[dim]  Pass 2 complete ({sw2.Elapsed.TotalSeconds:F1}s)[/]");

        return referencingTypes;
    }

    // Resolves each top-candidate address into a CandidateInfo record by reading type metadata,
    // computing retained size, mapping the generation, and picking the top-5 referencing types.
    // Returns the list sorted by inbound ref count descending.
    static List<(ulong Addr, CandidateInfo Info)> MaterializeCandidates(
        ClrHeap heap,
        HashSet<ulong> topAddrs,
        Dictionary<ulong, int> inboundCounts,
        Dictionary<ulong, Dictionary<string, int>> referencingTypes)
    {
        var candidates = new List<(ulong Addr, CandidateInfo Info)>(topAddrs.Count);
        foreach (var addr in topAddrs)
        {
            var obj = heap.GetObject(addr);
            if (!obj.IsValid) continue;

            string typeName     = obj.Type?.Name ?? "<unknown>";
            long   ownSize      = (long)obj.Size;
            long   retainedSize = ComputeRetained(heap, obj);

            var seg = heap.GetSegmentByAddress(addr);
            string gen = seg?.Kind switch
            {
                GCSegmentKind.Generation0 => "Gen0",
                GCSegmentKind.Generation1 => "Gen1",
                GCSegmentKind.Generation2 => "Gen2",
                GCSegmentKind.Large       => "LOH",
                GCSegmentKind.Pinned      => "POH",
                GCSegmentKind.Frozen      => "Frozen",
                GCSegmentKind.Ephemeral   => EphemeralGen(seg!, addr),
                _                          => "?",
            };

            var typeMap = referencingTypes.TryGetValue(addr, out var tm) ? tm
                        : new Dictionary<string, int>();
            var topSources = typeMap
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            int inRef = inboundCounts.TryGetValue(addr, out int r) ? r : 0;
            candidates.Add((addr, new CandidateInfo(
                typeName, ownSize, retainedSize, gen, inRef, typeMap.Count, topSources)));
        }

        candidates.Sort((a, b) => b.Info.InboundRefs.CompareTo(a.Info.InboundRefs));
        return candidates;
    }

    // Renders the key-value summary block and all threshold-based alerts.
    static void RenderSummaryAndAlerts(IRenderSink sink,
        List<(ulong Addr, CandidateInfo Info)> candidates,
        long totalObjs, long totalRefs, Dictionary<ulong, int> inboundCounts, int minRefs)
    {
        sink.Section("Summary");
        int  maxRefs      = candidates.Count > 0 ? candidates[0].Info.InboundRefs : 0;
        long totalHotSize = candidates.Sum(c => c.Info.RetainedSize);
        int  widelyShared = candidates.Count(c => c.Info.DistinctSourceTypes >= 10);
        int  cacheLike    = candidates.Count(c => IsCacheLike(c.Info.Type));

        sink.KeyValues([
            ("Objects scanned",              totalObjs.ToString("N0")),
            ("Total object references",      totalRefs.ToString("N0")),
            ("Unique referenced addresses",  inboundCounts.Count.ToString("N0")),
            ("Hot objects (≥ min-refs)",     $"{candidates.Count:N0}  (threshold: {minRefs:N0})"),
            ("Peak inbound ref count",       maxRefs.ToString("N0")),
            ("Widely shared (≥ 10 types)",   widelyShared.ToString("N0")),
            ("Cache-like hot objects",       cacheLike.ToString("N0")),
            ("Total retained size (est.)",  DumpHelpers.FormatSize(totalHotSize)),
        ]);

        if (maxRefs >= 10_000)
            sink.Alert(AlertLevel.Critical,
                $"Peak inbound reference count is {maxRefs:N0} — extreme shared-state detected.",
                $"A single object is referenced by {maxRefs:N0} other objects. One live root keeps ALL of them in memory.",
                "Review whether this object (or its owning container) should be scoped, pooled, or split.");
        else if (maxRefs >= 1_000)
            sink.Alert(AlertLevel.Warning,
                $"Peak inbound reference count is {maxRefs:N0}.",
                "Widely-shared objects extend the lifetime of every holder.",
                "Consider weak references or demand-loading for non-critical shared state.");

        if (widelyShared > 0)
            sink.Alert(AlertLevel.Warning,
                $"{widelyShared} object(s) are referenced from ≥ 10 distinct types — implicit global dependencies.",
                "Objects with many distinct referencing types are effectively ambient singletons. " +
                "They are difficult to mock, test, and scope independently.",
                "Prefer explicit dependency injection with scoped or transient lifetimes.");

        if (cacheLike > 0)
            sink.Alert(AlertLevel.Info,
                $"{cacheLike} hot object(s) appear to be caches or collections (Dictionary / List / ConcurrentDictionary).",
                "Shared mutable collections can grow without bound if no eviction policy is enforced.",
                "Verify that size limits, expiry policies, or bounded queues are in place.");
    }

    // Renders the main sorted table of highly-referenced objects.
    static void RenderMainTable(IRenderSink sink,
        List<(ulong Addr, CandidateInfo Info)> candidates, bool showAddr, int minRefs)
    {
        sink.Section($"Top {candidates.Count} Highly-Referenced Objects");
        var tableRows = candidates.Select(c =>
        {
            string shortType = ShortTypeName(c.Info.Type);
            string topSrc    = c.Info.TopSources.Count > 0 ? ShortTypeName(c.Info.TopSources[0].Type) : "—";
            var row = new List<string>
            {
                c.Info.Type,
                c.Info.InboundRefs.ToString("N0"),
                c.Info.DistinctSourceTypes.ToString("N0"),
                topSrc,
                c.Info.Gen,
                DumpHelpers.FormatSize(c.Info.OwnSize),
                DumpHelpers.FormatSize(c.Info.RetainedSize),
            };
            if (showAddr) row.Insert(0, $"0x{c.Addr:X16}");
            return row.ToArray();
        }).ToList();

        var headers = showAddr
            ? new[] { "Address", "Type", "Inbound Refs", "Distinct Ref Types", "Top Source Type", "Gen", "Own Size", "Retained† Size" }
            : new[] { "Type", "Inbound Refs", "Distinct Ref Types", "Top Source Type", "Gen", "Own Size", "Retained† Size" };
        sink.Table(headers, tableRows,
            $"Sorted by inbound reference count  |  min-refs = {minRefs}  |  † Retained = own + direct children");
    }

    // Renders per-object collapsible detail accordions (object metadata + top referencing types).
    static void RenderDetailAccordions(IRenderSink sink,
        List<(ulong Addr, CandidateInfo Info)> candidates)
    {
        sink.Section("Object Detail");

        foreach (var (addr, info) in candidates)
        {
            bool hotOpen = info.InboundRefs >= 500 || info.DistinctSourceTypes >= 10;
            string category = Categorize(info.Type);

            string title =
                $"{info.Type}  |  {info.InboundRefs:N0} inbound refs  |  {info.Gen}  |  {DumpHelpers.FormatSize(info.RetainedSize)} retained";
            sink.BeginDetails(title, open: hotOpen);

            // ── Object metadata table ─────────────────────────────────────
            sink.Table(["Property", "Value"],
            [
                ["Full Type",               info.Type],
                ["Address",                 $"0x{addr:X16}"],
                ["Own Size",                DumpHelpers.FormatSize(info.OwnSize)],
                ["Retained Size (est.)",    DumpHelpers.FormatSize(info.RetainedSize)],
                ["Generation",              info.Gen],
                ["Inbound Refs",            info.InboundRefs.ToString("N0")],
                ["Distinct Ref Types",      info.DistinctSourceTypes.ToString("N0")],
                ["Category",                category],
            ]);

            // ── Top referencing types ─────────────────────────────────────
            if (info.TopSources.Count > 0)
            {
                var srcRows = info.TopSources
                    .Select(s => new[]
                    {
                        s.Type,
                        s.Count.ToString("N0"),
                        $"{s.Count * 100.0 / Math.Max(1, info.InboundRefs):F1}%",
                    })
                    .ToList();
                sink.Table(
                    ["Referencing Type", "Count", "% of Total"],
                    srcRows,
                    $"Top {srcRows.Count} of {info.DistinctSourceTypes} distinct referencing types");
            }

            // ── Per-object advisory ───────────────────────────────────────
            if (category is "Cache" or "Collection")
                sink.Alert(AlertLevel.Info,
                    $"This {category.ToLower()} is referenced by {info.InboundRefs:N0} objects.",
                    "Shared mutable collections can grow without bound. Verify eviction and size limits.");
            else if (category == "String")
                sink.Alert(AlertLevel.Info,
                    $"String instance referenced {info.InboundRefs:N0} times.",
                    "Widely-shared strings are usually fine (flyweight). " +
                    "If this string is large, consider whether all holders need their own reference.");
            else if (info.DistinctSourceTypes >= 15)
                sink.Alert(AlertLevel.Warning,
                    $"Referenced from {info.DistinctSourceTypes} distinct types — implicit global dependency.",
                    "This object is acting as ambient shared state, coupling many unrelated types together.",
                    "Refactor to pass the dependency explicitly via constructor injection.");
            else if (category is "DI Container")
                sink.Alert(AlertLevel.Info,
                    $"DI/IoC container referenced from {info.DistinctSourceTypes} types.",
                    "Passing the container around as a service locator is an anti-pattern. " +
                    "Inject the specific service interfaces instead.");

            sink.EndDetails();
        }
    }

    // Renders the hot-type distribution: candidates grouped by simplified type name.
    static void RenderHubDistribution(IRenderSink sink,
        List<(ulong Addr, CandidateInfo Info)> candidates)
    {
        sink.Section("Hub Type Distribution");
        var hubTypes = candidates
            .GroupBy(c => SimplifiedTypeName(c.Info.Type))
            .Select(g =>
            {
                int sumRef  = g.Sum(c => c.Info.InboundRefs);
                int maxRef  = g.Max(c => c.Info.InboundRefs);
                long totRet = g.Sum(c => c.Info.RetainedSize);
                string cat  = Categorize(g.First().Info.Type);
                return new[] { g.Key, cat, g.Count().ToString("N0"), sumRef.ToString("N0"), maxRef.ToString("N0"), DumpHelpers.FormatSize(totRet) };
            })
            .OrderByDescending(r => int.Parse(r[3].Replace(",", "")))
            .ToList();
        if (hubTypes.Count > 0)
            sink.Table(
                ["Type Pattern", "Category", "Hot Instances", "Sum Inbound Refs", "Max Inbound Refs", "Retained Size (est.)"],
                hubTypes,
                "Hot types grouped by simplified name — reveals structural retention patterns");
    }

    // Renders the generation distribution + GC write-barrier advisory.
    static void RenderGenDistribution(IRenderSink sink,
        List<(ulong Addr, CandidateInfo Info)> candidates, Dictionary<ulong, int> inboundCounts)
    {
        sink.Section("Generation Distribution");
        var genDist = candidates
            .GroupBy(c => c.Info.Gen)
            .OrderBy(g => GenOrder(g.Key))
            .Select(g => new[]
            {
                g.Key,
                g.Count().ToString("N0"),
                g.Sum(c => c.Info.InboundRefs).ToString("N0"),
                DumpHelpers.FormatSize(g.Sum(c => c.Info.RetainedSize)),
            })
            .ToList();
        if (genDist.Count > 0)
            sink.Table(
                ["Generation", "Hot Objects", "Total Inbound Refs", "Retained Size (est.)"],
                genDist,
                "Gen2/LOH objects with many inbound refs from younger generations increase GC write-barrier cost");

        int gen2Count = candidates.Count(c => c.Info.Gen is "Gen2" or "LOH");
        if (gen2Count > 0)
            sink.Alert(AlertLevel.Info,
                $"{gen2Count} of the hot objects reside in Gen2 or LOH.",
                "References from Gen0/Gen1 objects to these Gen2/LOH objects require GC write barriers and increase " +
                "card-table pressure. Every minor GC must scan these remembered-set entries.",
                "Minimise the number of short-lived objects that hold references to these long-lived hubs.");
    }

    // Renders the inbound-reference count distribution histogram across all objects.
    static void RenderRefHistogram(IRenderSink sink, Dictionary<ulong, int> inboundCounts)
    {
        sink.Section("Reference Count Distribution");
        var buckets = new (string Label, int Lo, int Hi)[]
        {
            ("10 – 49",      10,    49),
            ("50 – 99",      50,    99),
            ("100 – 499",   100,   499),
            ("500 – 999",   500,   999),
            ("1 000 – 9 999", 1_000, 9_999),
            ("≥ 10 000",    10_000, int.MaxValue),
        };
        var histRows = buckets
            .Select(b =>
            {
                int cnt = inboundCounts.Values.Count(v => v >= b.Lo && v <= b.Hi);
                return cnt > 0 ? new[] { b.Label, cnt.ToString("N0") } : null;
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        if (histRows.Count > 0)
            sink.Table(["Inbound Ref Range", "Object Count"], histRows,
                "Distribution of all objects by their inbound reference count (all objects ≥ 10)");
    }

    // Determines Gen0/1/2 for an address inside an ephemeral segment.
    static string EphemeralGen(ClrSegment seg, ulong addr)
    {
        if (seg.Generation0.Contains(addr)) return "Gen0";
        if (seg.Generation1.Contains(addr)) return "Gen1";
        if (seg.Generation2.Contains(addr)) return "Gen2";
        return "Gen";
    }

    // Numeric sort key for generation labels: Gen0 < Gen1 < Gen2 < LOH < POH < Frozen.
    static int GenOrder(string gen) => gen switch
    {
        "Gen0" => 0, "Gen1" => 1, "Gen2" => 2,
        "LOH"  => 3, "POH"  => 4, "Frozen" => 5,
        _      => 9,
    };

    // Returns true when the type name suggests a cache, dictionary, or generic collection.
    static bool IsCacheLike(string type) =>
        type.Contains("Dictionary",         StringComparison.OrdinalIgnoreCase) ||
        type.Contains("ConcurrentDictionary", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("Cache",              StringComparison.OrdinalIgnoreCase) ||
        type.Contains("MemoryCache",        StringComparison.OrdinalIgnoreCase) ||
        type.Contains("List",               StringComparison.OrdinalIgnoreCase) ||
        type.Contains("HashSet",            StringComparison.OrdinalIgnoreCase) ||
        type.Contains("Queue",              StringComparison.OrdinalIgnoreCase);

    // Maps a CLR type name to a high-level category label used in advisories and the hub table.
    static string Categorize(string type)
    {
        if (type == "System.String" || type.StartsWith("System.String[", StringComparison.Ordinal))
            return "String";
        if (type.Contains("Dictionary",    StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Cache",         StringComparison.OrdinalIgnoreCase) ||
            type.Contains("MemoryCache",   StringComparison.OrdinalIgnoreCase))
            return "Cache";
        if (type.Contains("List",          StringComparison.OrdinalIgnoreCase) ||
            type.Contains("[]")                                                 ||
            type.Contains("HashSet",       StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Queue",         StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Collection",    StringComparison.OrdinalIgnoreCase))
            return "Collection";
        if (type.Contains("Logger",        StringComparison.OrdinalIgnoreCase) ||
            (type.Contains("Log",          StringComparison.OrdinalIgnoreCase) &&
             !type.Contains("Dialog",      StringComparison.OrdinalIgnoreCase)))
            return "Logger";
        if (type.Contains("Configuration", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Options",       StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Settings",      StringComparison.OrdinalIgnoreCase))
            return "Config/Options";
        if (type.Contains("ServiceProvider", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Container",     StringComparison.OrdinalIgnoreCase)   ||
            type.Contains("Factory",       StringComparison.OrdinalIgnoreCase))
            return "DI Container";
        if (type.Contains("HttpClient",    StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DbContext",     StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Connection",    StringComparison.OrdinalIgnoreCase))
            return "Resource Client";
        return "Object";
    }

    // Returns the simple unqualified class name without namespace or generic arities.
    static string ShortTypeName(string type)
    {
        // Pop everything after '<' or '`'
        int bt = type.IndexOf('`');
        int lt = type.IndexOf('<');
        int end = bt >= 0 && (lt < 0 || bt < lt) ? bt : lt >= 0 ? lt : type.Length;
        string trimmed = type[..end];
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }

    // Like ShortTypeName but retains the full namespace prefix so hub-table grouping is per qualified base type.
    static string SimplifiedTypeName(string type)
    {
        int bt = type.IndexOf('`');
        int lt = type.IndexOf('<');
        int end = bt >= 0 && (lt < 0 || bt < lt) ? bt : lt >= 0 ? lt : type.Length;
        return type[..end];
    }
}
