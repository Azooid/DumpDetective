using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;
using System.Diagnostics;

namespace DumpDetective.Commands;

// Scans a heap dump for event handler leaks: delegate fields with live subscribers that
// are held alive by static publishers or long-lived roots and therefore never collected.
internal static class EventAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective event-analysis <dump-file> [options]

        Options:
          -n, --top <N>          Top N event fields to show (default: 20)
          -e, --exclude <type>   Exclude types containing <type> (repeatable)
          -a, --addresses        Show subscriber object addresses
          -o, --output <f>       Write report to file (.html / .md / .txt / .json)
          -h, --help             Show this help
        """;

    // Per-subscriber detail enriched with size, static-root status, method name and lambda flag.
    private readonly record struct SubDetail(
        string TargetType, ulong TargetAddr, ulong DelAddr, ulong Size, bool IsStaticRooted,
        string MethodName, bool IsLambda);

    // One delegate field on one publisher instance, together with its resolved subscribers.
    private readonly record struct LeakEntry(
        string PublisherType,
        ulong PublisherAddr,
        string FieldName,
        string DelegateType,
        bool IsStaticPublisher,   // true when the publisher object is itself held by a static root
        List<SubDetail> Subs);

    // Aggregated stats for a (PublisherType, FieldName) pair across all instances.
    private readonly record struct EventGroup(
        string PublisherType,
        string FieldName,
        int Instances,            // number of distinct publisher objects carrying this field
        int Subscribers,          // total live subscriber count across all instances
        ulong RetainedBytes,
        bool IsStaticPublisher,   // true when any instance is statically rooted
        bool HasStaticSubs,       // true when any subscriber is itself statically rooted
        List<SubDetail> AllSubs,
        int LambdaCount,          // number of subscribers that are compiler-generated closures
        int DuplicateCount);      // number of (addr, method) pairs registered more than once

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        int top = 20;
        bool showAddr = false;
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--top" or "-n") && i + 1 < args.Length)
                int.TryParse(args[++i], out top);
            else if (args[i] is "--addresses" or "-a")
                showAddr = true;
            else if ((args[i] is "--exclude" or "-e") && i + 1 < args.Length)
                excludes.Add(args[++i]);
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, excludes, showAddr, top));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink,
                                HashSet<string>? excludes = null, bool showAddr = false, int top = 20)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Event Analysis Report",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var staticRoots = BuildStaticRoots(ctx);
        var leaks       = ScanLeaks(ctx, staticRoots, excludes);
        var groups      = BuildGroups(leaks);

        sink.Section("1. Event Handler Leaks");

        if (groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No event handler leaks found.");
            return;
        }

        RenderSummaryTable(sink, groups, leaks, top);
        RenderBreakdown(sink, groups, top);
        RenderMethodStats(sink, groups, top);

        if (showAddr && leaks.Count > 0)
            RenderAddresses(sink, leaks);

        RenderFooter(sink, groups, leaks.Count);
    }

    // ── Data gathering ────────────────────────────────────────────────────────

    static HashSet<ulong> BuildStaticRoots(DumpContext ctx)
    {
        // Build a set of object addresses that are reachable from static fields.
        // ClrMD 3.x doesn't expose a StaticVariable root kind, so we enumerate types directly
        // across every module in every AppDomain and read each static object-reference field.
        var staticRoots = new HashSet<ulong>();
        foreach (var appDomain in ctx.Runtime.AppDomains)
        {
            foreach (var module in appDomain.Modules)
            {
                foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
                {
                    if (mt == 0) continue;
                    var clrType = ctx.Heap.GetTypeByMethodTable(mt);
                    if (clrType is null) continue;
                    foreach (var sf in clrType.StaticFields)
                    {
                        if (!sf.IsObjectReference) continue;
                        try
                        {
                            var obj = sf.ReadObject(appDomain);
                            if (obj.IsValid && !obj.IsNull) staticRoots.Add(obj.Address);
                        }
                        catch { }
                    }
                }
            }
        }
        return staticRoots;
    }

    // Walks every heap object, finds delegate fields that look like event subscriptions,
    // and resolves each delegate's subscriber list into LeakEntry records.
    static List<LeakEntry> ScanLeaks(DumpContext ctx, HashSet<ulong> staticRoots, HashSet<string>? excludes)
    {
        var leaks = new List<LeakEntry>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .Start("Scanning for event leaks...", statusCtx =>
            {
                var watch    = Stopwatch.StartNew();
                long visited = 0;

                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                    var typeName = obj.Type.Name ?? string.Empty;
                    if (DumpHelpers.IsSystemType(typeName)) continue;
                    if (excludes?.Any(e => typeName.Contains(e, StringComparison.OrdinalIgnoreCase)) == true) continue;

                    bool isStaticPub = staticRoots.Contains(obj.Address);

                    foreach (var field in obj.Type.Fields)
                    {
                        if (!field.IsObjectReference || field.Type is null || !IsDelegate(field.Type)) continue;
                        // Skip bare Action / Func / callback fields — not event subscriptions
                        var ft = field.Type.Name ?? string.Empty;
                        if (ft.StartsWith("System.Action",           StringComparison.Ordinal) ||
                            ft.StartsWith("System.Func",             StringComparison.Ordinal) ||
                            ft.StartsWith("System.Threading.Thread", StringComparison.Ordinal))
                            continue;
                        // Also skip fields literally named "action" / "callback" / "handler" (compiler closures)
                        var fn = field.Name ?? string.Empty;
                        if (fn.Equals("action",   StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("callback", StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("handler",  StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("func",     StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("del",      StringComparison.OrdinalIgnoreCase) ||
                            fn.Equals("delegate", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            var delVal = field.ReadObject(obj.Address, false);
                            if (delVal.IsNull || !delVal.IsValid) continue;
                            var subs = CollectSubscribers(delVal, staticRoots, ctx.Runtime);
                            if (excludes?.Count > 0)
                                subs = [..subs.Where(s => excludes.All(
                                    e => !s.TargetType.Contains(e, StringComparison.OrdinalIgnoreCase)))];
                            if (subs.Count == 0) continue;
                            leaks.Add(new LeakEntry(typeName, obj.Address, field.Name ?? "<?>",
                                                    field.Type.Name ?? "?", isStaticPub, subs));
                        }
                        catch { }
                    }

                    visited++;
                    if (watch.Elapsed.TotalSeconds >= 1)
                    {
                        statusCtx.Status($"Scanning for event leaks — {visited:N0} objects scanned, {leaks.Count} leak(s) found");
                        watch.Restart();
                    }
                }
            });

        return leaks;
    }

    // Aggregates individual LeakEntry records into per-(PublisherType, FieldName) EventGroups,
    // computing totals, duplicate counts and severity flags.
    static List<EventGroup> BuildGroups(List<LeakEntry> leaks) =>
        leaks
            .GroupBy(l => (l.PublisherType, l.FieldName))
            .Select(g =>
            {
                var allSubs    = g.SelectMany(l => l.Subs).ToList();
                ulong retained = (ulong)allSubs.Sum(s => (long)s.Size);
                bool  isStatic = g.Any(l => l.IsStaticPublisher);
                bool  hasSR    = allSubs.Any(s => s.IsStaticRooted);
                int   lambdas  = allSubs.Count(s => s.IsLambda);
                int   dupes    = g.Sum(l =>
                {
                    // Count (TargetAddr, MethodName) pairs that appear more than once in the same
                    // publisher instance — a sign that += was called without a matching -=.
                    var seen = new HashSet<(ulong, string)>();
                    return l.Subs.Count(s => !seen.Add((s.TargetAddr, s.MethodName)));
                });
                return new EventGroup(
                    PublisherType:     g.Key.PublisherType,
                    FieldName:         g.Key.FieldName,
                    Instances:         g.Count(),
                    Subscribers:       allSubs.Count,
                    RetainedBytes:     retained,
                    IsStaticPublisher: isStatic,
                    HasStaticSubs:     hasSR,
                    AllSubs:           allSubs,
                    LambdaCount:       lambdas,
                    DuplicateCount:    dupes);
            })
            .OrderByDescending(g => g.IsStaticPublisher)
            .ThenByDescending(g => g.Subscribers)
            .ToList();

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Maps severity based on whether the publisher or any subscriber is statically rooted.
    static string Severity(bool isStatic, bool hasSR) =>
        isStatic ? "⚡ CRITICAL" : hasSR ? "⚠ WARNING" : "—";

    // Section 1: one row per unique (PublisherType, FieldName) pair.
    static void RenderSummaryTable(IRenderSink sink, List<EventGroup> groups, List<LeakEntry> leaks, int top)
    {
        var summaryRows = groups.Take(top).Select(g => new[]
        {
            g.PublisherType, g.FieldName,
            g.Instances.ToString("N0"),
            g.Subscribers.ToString("N0"),
            DumpHelpers.FormatSize((long)g.RetainedBytes),
            Severity(g.IsStaticPublisher, g.HasStaticSubs),
        }).ToList();

        sink.Table(
            ["Publisher Type", "Event Field", "Instances", "Subscribers", "Retained", "Severity"],
            summaryRows,
            $"{groups.Count} unique event field(s) across {leaks.Count} publisher instance(s)");
    }

    // Section 2: per-event collapsible panel with subscriber-type breakdown and fix advice.
    static void RenderBreakdown(IRenderSink sink, List<EventGroup> groups, int top)
    {
        // ── 2. Subscriber type breakdown + fix advice ─────────────────────────
        sink.Section("2. Subscriber Breakdown");
        foreach (var g in groups.Take(top))
        {
            string sev = Severity(g.IsStaticPublisher, g.HasStaticSubs);
            sink.BeginDetails(
                $"{g.PublisherType}.{g.FieldName}  " +
                $"({g.Subscribers:N0} subscribers  |  {DumpHelpers.FormatSize((long)g.RetainedBytes)} retained  |  {sev})",
                open: g.IsStaticPublisher);

            var bySubType = g.AllSubs
                .GroupBy(s => (s.TargetType, s.MethodName))
                .Select(tg => (
                    Type:      tg.Key.TargetType,
                    Method:    tg.Key.MethodName,
                    Count:     tg.Count(),
                    Size:      (ulong)tg.Sum(s => (long)s.Size),
                    HasStatic: tg.Any(s => s.IsStaticRooted),
                    IsLambda:  tg.All(s => s.IsLambda)))
                .OrderByDescending(t => t.Count)
                .Take(10)
                .ToList();

            sink.Table(
                ["Subscriber Type", "Subscribed Method", "Count", "Size", "Static?", "Lambda?"],
                bySubType.Select(t => new[]
                {
                    t.Type, t.Method, t.Count.ToString("N0"),
                    DumpHelpers.FormatSize((long)t.Size),
                    t.HasStatic ? "⚠ yes" : "—",
                    t.IsLambda  ? "λ yes" : "—",
                }).ToList());

            if (g.DuplicateCount > 0)
                sink.Alert(AlertLevel.Warning,
                    $"{g.DuplicateCount} duplicate subscription(s) on '{g.FieldName}'.",
                    detail: "The same handler is registered more than once to this event.",
                    advice: "Ensure += is not called multiple times without a matching -=.");

            if (g.IsStaticPublisher)
                sink.Alert(AlertLevel.Critical,
                    $"Static publisher: subscribers on '{g.FieldName}' will NEVER be garbage collected.",
                    advice: $"publisher.{g.FieldName} -= OnHandler;  // call in subscriber's Dispose()");
            else if (g.HasStaticSubs)
                sink.Alert(AlertLevel.Warning,
                    $"Long-lived subscribers on '{g.FieldName}' are kept alive by static roots.",
                    advice: $"publisher.{g.FieldName} -= OnHandler;  // call in subscriber's Dispose()");

            sink.EndDetails();
        }
    }

    // Section 3: method-level roll-up showing which handler methods have the most subscriptions.
    static void RenderMethodStats(IRenderSink sink, List<EventGroup> groups, int top)
    {
        // ── 3. Top subscribed methods ─────────────────────────────────────────
        sink.Section("3. Top Subscribed Methods");
        var methodStats = groups
            .SelectMany(g => g.AllSubs)
            .GroupBy(s => s.MethodName)
            .Select(mg => (
                Method:   mg.Key,
                Count:    mg.Count(),
                Size:     (ulong)mg.Sum(s => (long)s.Size),
                IsLambda: mg.Any(s => s.IsLambda)))
            .OrderByDescending(m => m.Count)
            .Take(top)
            .ToList();

        sink.Table(
            ["Subscribed Method", "Total Subscriptions", "Retained", "Lambda?"],
            methodStats.Select(m => new[]
            {
                m.Method, m.Count.ToString("N0"),
                DumpHelpers.FormatSize((long)m.Size),
                m.IsLambda ? "λ yes" : "—",
            }).ToList(),
            "Methods sorted by subscription count across all events");
    }

    // Section 4 (--addresses): raw publisher/subscriber address pairs for use with !do / !gcroot.
    static void RenderAddresses(IRenderSink sink, List<LeakEntry> leaks)
    {
        // ── 4. Address detail (--addresses) ───────────────────────────────────
        sink.Section("4. Subscriber Addresses");
        var detailRows = leaks.SelectMany(l =>
            l.Subs.Select(s => new[]
            {
                l.PublisherType, $"0x{l.PublisherAddr:X16}", l.FieldName,
                s.TargetType, $"0x{s.TargetAddr:X16}",
                s.IsStaticRooted ? "⚠ static" : "—",
            })
        ).Take(200).ToList();

        sink.Table(
            ["Publisher Type", "Publisher Addr", "Field", "Subscriber Type", "Subscriber Addr", "Static?"],
            detailRows);
    }

    // Emits the overall severity alert and key-value summary at the bottom of the report.
    static void RenderFooter(IRenderSink sink, List<EventGroup> groups, int publisherInstanceCount)
    {
        int   totalSubs     = groups.Sum(g => g.Subscribers);
        ulong totalRetained = (ulong)groups.Sum(g => (long)g.RetainedBytes);
        int   criticalCount = groups.Count(g => g.IsStaticPublisher);
        int   warningCount  = groups.Count(g => !g.IsStaticPublisher && g.HasStaticSubs);

        if (criticalCount > 0)
            sink.Alert(AlertLevel.Critical,
                $"{criticalCount} static publisher(s) — subscribers are permanently rooted and will never be collected.");
        else if (totalSubs > 1000)
            sink.Alert(AlertLevel.Critical, $"High subscriber count: {totalSubs:N0} total event subscriptions.",
                advice: "Unsubscribe event handlers when the subscriber is disposed (-= handler).");
        else if (totalSubs > 200 || warningCount > 0)
            sink.Alert(AlertLevel.Warning, $"{totalSubs:N0} event subscriptions found.");

        int totalLambdas = groups.Sum(g => g.LambdaCount);
        int totalDupes   = groups.Sum(g => g.DuplicateCount);
        sink.KeyValues([
            ("Unique event fields",      groups.Count.ToString("N0")),
            ("Publisher instances",      publisherInstanceCount.ToString("N0")),
            ("Total subscribers",        totalSubs.ToString("N0")),
            ("Lambda/closure subs",      totalLambdas.ToString("N0")),
            ("Duplicate subscriptions",  totalDupes.ToString("N0")),
            ("Total retained memory",    DumpHelpers.FormatSize((long)totalRetained)),
            ("Static publishers",        criticalCount.ToString("N0")),
        ]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Expands a multicast delegate's _invocationList array into individual SubDetail records.
    // Falls back to treating the delegate itself as a single-subscriber delegate when the list is absent.
    static List<SubDetail> CollectSubscribers(ClrObject del, HashSet<ulong> staticRoots, ClrRuntime runtime)
    {
        var result = new List<SubDetail>();
        try
        {
            var invList = del.ReadObjectField("_invocationList");
            if (!invList.IsNull && invList.IsValid && invList.Type?.IsArray == true)
            {
                var arr = invList.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    var item = arr.GetObjectValue(i);
                    if (!item.IsValid || item.IsNull) continue;
                    var sub = TryGetSub(item, staticRoots, runtime);
                    if (sub.HasValue) result.Add(sub.Value);
                }
            }
            else
            {
                var sub = TryGetSub(del, staticRoots, runtime);
                if (sub.HasValue) result.Add(sub.Value);
            }
        }
        catch { }
        return result;
    }

    // Reads _target and _methodPtr from a single delegate object and builds a SubDetail.
    // Returns null for system-type targets (e.g. internal runtime callbacks) that aren't leaks.
    static SubDetail? TryGetSub(ClrObject del, HashSet<ulong> staticRoots, ClrRuntime runtime)
    {
        var target = del.ReadObjectField("_target");
        if (target.IsNull) return null;
        var targetType = target.Type?.Name ?? "<unknown>";
        if (DumpHelpers.IsSystemType(targetType)) return null;
        bool   isLambda = IsLambdaType(targetType);
        string method   = ResolveMethodName(del, runtime);
        return new SubDetail(targetType, target.Address, del.Address,
                             target.Size, staticRoots.Contains(target.Address),
                             method, isLambda);
    }

    // Resolves the target method name from a delegate's _methodPtr via the JIT method table.
    static string ResolveMethodName(ClrObject del, ClrRuntime runtime)
    {
        try
        {
            ulong ptr = del.ReadField<ulong>("_methodPtr");
            if (ptr == 0) return "?";
            var m = runtime.GetMethodByInstructionPointer(ptr);
            if (m is null) return $"0x{ptr:X}";
            string typePart = m.Type?.Name is { } tn ? $"{tn}." : string.Empty;
            return $"{typePart}{m.Name}";
        }
        catch { return "?"; }
    }

    // Detects compiler-generated closure/display-class types produced by lambda expressions.
    static bool IsLambdaType(string typeName) =>
        typeName.Contains("<>c",             StringComparison.Ordinal) ||
        typeName.Contains("+<>",             StringComparison.Ordinal) ||
        typeName.Contains("__DisplayClass",  StringComparison.Ordinal);

    // Walks the base-type chain to determine whether a ClrType derives from MulticastDelegate.
    static bool IsDelegate(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "System.MulticastDelegate" or "System.Delegate") return true;
        return false;
    }
}
