using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

// Enumerates WeakShort/WeakLong GC handles and ConditionalWeakTable instances.
// Reports alive-vs-collected ratios, type breakdown, and large entry-count tables
// that may indicate per-object metadata leaks.
internal static class WeakRefsCommand
{
    private const string Help = """
        Usage: DumpDetective weak-refs <dump-file> [options]

        Options:
          -a, --addresses    Show handle addresses
          -o, --output <f>   Write report to file (.html / .md / .txt / .json)
          -h, --help         Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;
        bool showAddr = args.Any(a => a is "--addresses" or "-a");
        var (dumpPath, output) = CommandBase.ParseCommon(args);
        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Weak References",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        var refs = ctx.Runtime.EnumerateHandles()
            .Where(h => h.HandleKind is ClrHandleKind.WeakShort or ClrHandleKind.WeakLong)
            .Select(h =>
            {
                bool alive = h.Object != 0;
                var obj = alive ? ctx.Heap.GetObject(h.Object) : default;
                bool valid = alive && obj.IsValid;
                return (
                    Kind:  h.HandleKind.ToString(),
                    Alive: valid,
                    Type:  valid ? obj.Type?.Name ?? "?" : "<collected>",
                    Addr:  h.Object
                );
            }).ToList();

        int aliveCount     = refs.Count(r => r.Alive);
        int collectedCount = refs.Count - aliveCount;
        int total          = refs.Count;
        int alivePercent   = total > 0 ? aliveCount * 100 / total : 0;

        RenderHandleSummary(sink, total, aliveCount, alivePercent, collectedCount);
        RenderHandleTypeBreakdown(sink, refs);
        RenderCollectedHandles(sink, refs, alivePercent, collectedCount);
        RenderAddressTable(sink, refs, showAddr);

        if (!ctx.Heap.CanWalkHeap) return;

        var cwtInstances = ScanConditionalWeakTables(ctx);
        RenderConditionalWeakTables(sink, cwtInstances);
    }

    // Enumerates all ConditionalWeakTable instances on the heap and reads their entry counts.
    // Returns one tuple per CWT instance found (TypeParam substring + entry count).
    static List<(string TypeParam, int Entries)> ScanConditionalWeakTables(DumpContext ctx)
    {
        var cwtInstances = new List<(string TypeParam, int Entries)>();
        CommandBase.RunStatus("Scanning for ConditionalWeakTable...", () =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var name = obj.Type.Name ?? string.Empty;
                if (!name.StartsWith("System.Runtime.CompilerServices.ConditionalWeakTable",
                        StringComparison.Ordinal)) continue;

                int entryCount = 0;
                try
                {
                    var container = obj.ReadObjectField("_container");
                    if (!container.IsNull && container.IsValid)
                    {
                        var entries = container.ReadObjectField("_entries");
                        if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                            entryCount = entries.AsArray().Length;
                    }
                }
                catch { }
                if (entryCount == 0)
                {
                    try
                    {
                        var entries = obj.ReadObjectField("_entries");
                        if (!entries.IsNull && entries.IsValid && entries.Type?.IsArray == true)
                            entryCount = entries.AsArray().Length;
                    }
                    catch { }
                }

                string typeParam = name.Contains('[') ? name[name.IndexOf('[')..] : "";
                cwtInstances.Add((typeParam, entryCount));
            }
        });
        return cwtInstances;
    }

    // Renders the top-level weak-reference count summary and health alert.
    static void RenderHandleSummary(IRenderSink sink,
        int total, int aliveCount, int alivePercent, int collectedCount)
    {
        sink.Section("Weak Reference Summary");
        sink.KeyValues([
            ("Total weak handles", total.ToString("N0")),
            ("Alive",              $"{aliveCount:N0}  ({alivePercent}%)"),
            ("Collected",          collectedCount.ToString("N0")),
        ]);

        if (total > 10 && alivePercent < 20)
            sink.Alert(AlertLevel.Warning,
                $"Only {alivePercent}% of weak references are alive — high object churn or abandoned caches.",
                advice: "Review WeakReference<T> / ConditionalWeakTable usage. Objects may be collected sooner than expected.");
        else if (total > 1000)
            sink.Alert(AlertLevel.Info, $"{total:N0} weak handles in use.");
    }

    // Renders the alive-object type breakdown (top 30 types by handle count).
    static void RenderHandleTypeBreakdown(IRenderSink sink,
        List<(string Kind, bool Alive, string Type, ClrObject Addr)> refs)
    {
        var aliveByType = refs.Where(r => r.Alive)
            .GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count())
            .Take(30)
            .Select(g => new[] { g.Key, g.Count().ToString("N0") })
            .ToList();
        if (aliveByType.Count > 0)
            sink.Table(["Alive Object Type", "Count"], aliveByType, "Top types currently alive via weak reference");
    }

    // Renders the collected-handle breakdown by WeakShort vs WeakLong with an advisory.
    static void RenderCollectedHandles(IRenderSink sink,
        List<(string Kind, bool Alive, string Type, ClrObject Addr)> refs,
        int alivePercent, int collectedCount)
    {
        if (collectedCount == 0) return;

        int collectedByShort = refs.Count(r => !r.Alive && r.Kind == "WeakShort");
        int collectedByLong  = refs.Count(r => !r.Alive && r.Kind == "WeakLong");

        sink.KeyValues([
            ("Collected (WeakShort)", $"{collectedByShort:N0}  (tracking-resurrection disabled)"),
            ("Collected (WeakLong)",  $"{collectedByLong:N0}  (tracking-resurrection enabled)"),
            ("Note", "Collected object types are unavailable — object graphs were reclaimed by GC"),
        ]);

        if (collectedByLong > collectedByShort && collectedByLong > 100)
            sink.Alert(AlertLevel.Info,
                $"{collectedByLong:N0} WeakLong handles with collected targets — these prevent resurrection tracking.",
                "WeakLong handles keep the object's finalizer-resurrection reference alive longer than needed.",
                "Prefer WeakReference<T>(trackResurrection: false) unless you need post-finalize tracking.");
    }

    // Renders the first-200 handles as an address table when --addresses is set.
    static void RenderAddressTable(IRenderSink sink,
        List<(string Kind, bool Alive, string Type, ClrObject Addr)> refs, bool showAddr)
    {
        if (!showAddr || refs.Count == 0) return;

        var rows = refs.Take(200).Select(r => new[]
        {
            r.Kind,
            r.Alive ? "Alive" : "Collected",
            r.Type,
            $"0x{(ulong)r.Addr:X16}",
        }).ToList();
        sink.Table(["Kind", "Status", "Type", "Address"], rows, $"First {rows.Count} handles");
    }

    // Renders ConditionalWeakTable instance summary and large-entry-count advisory.
    static void RenderConditionalWeakTables(IRenderSink sink,
        List<(string TypeParam, int Entries)> cwtInstances)
    {
        if (cwtInstances.Count == 0) return;

        sink.Section("ConditionalWeakTable Instances");
        sink.KeyValues([("Total ConditionalWeakTable instances", cwtInstances.Count.ToString("N0"))]);

        var cwtRows = cwtInstances
            .GroupBy(c => c.TypeParam)
            .OrderByDescending(g => g.Sum(c => c.Entries))
            .Take(20)
            .Select(g => new[]
            {
                g.Key.Length > 0 ? g.Key : "<unknown type params>",
                g.Count().ToString("N0"),
                g.Sum(c => c.Entries).ToString("N0"),
            })
            .ToList();
        sink.Table(["Type Parameters", "Instances", "Total Entries"], cwtRows,
            "ConditionalWeakTable instances by type parameter combination");

        int totalEntries = cwtInstances.Sum(c => c.Entries);
        if (totalEntries > 100_000)
            sink.Alert(AlertLevel.Warning,
                $"{totalEntries:N0} total entries across {cwtInstances.Count} ConditionalWeakTable instance(s).",
                "ConditionalWeakTable is commonly used for per-object metadata (e.g., by frameworks and aspect libraries).",
                "Large entry counts may indicate a leak in framework-level metadata attachment. " +
                "Keys are held weakly, but values are kept alive as long as the key is reachable.");
    }
}
