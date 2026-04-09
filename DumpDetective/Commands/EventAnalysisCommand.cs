using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Output;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class EventAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective event-analysis <dump-file> [options]

        Options:
          -e, --exclude <type>   Exclude types containing <type> (repeatable)
          -a, --addresses        Show subscriber addresses
          -o, --output <f>       Write report to file (.md / .html / .txt)
          -h, --help             Show this help
        """;

    public static int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        bool showAddr = false;
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (dumpPath, output) = CommandBase.ParseCommon(args);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--addresses" or "-a") showAddr = true;
            else if ((args[i] is "--exclude" or "-e") && i + 1 < args.Length) excludes.Add(args[++i]);
        }

        return CommandBase.Execute(dumpPath, output, (ctx, sink) => Render(ctx, sink, excludes, showAddr));
    }

    internal static void Render(DumpContext ctx, IRenderSink sink, HashSet<string>? excludes = null, bool showAddr = false)
    {
        CommandBase.PrintAnalyzing(ctx.DumpPath);

        sink.Header(
            "Dump Detective — Event Analysis Report",
            $"{Path.GetFileName(ctx.DumpPath)}  |  {ctx.FileTime:yyyy-MM-dd HH:mm:ss}  |  CLR {ctx.ClrVersion ?? "unknown"}");

        if (!ctx.Heap.CanWalkHeap) { sink.Alert(AlertLevel.Warning, "Cannot walk heap."); return; }

        var leaks = new List<(string PublisherType, ulong PublisherAddr, string FieldName, string DelegateType, List<(string TargetType, ulong TargetAddr, ulong DelAddr)> Subs)>();

        AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Scanning for event leaks...", _ =>
        {
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null || obj.Type.IsFree) continue;
                var typeName = obj.Type.Name ?? string.Empty;
                if (DumpHelpers.IsSystemType(typeName)) continue;
                if (excludes?.Any(e => typeName.Contains(e, StringComparison.OrdinalIgnoreCase)) == true) continue;

                foreach (var field in obj.Type.Fields)
                {
                    if (!field.IsObjectReference || field.Type is null || !IsDelegate(field.Type)) continue;
                    try
                    {
                        var delVal = field.ReadObject(obj.Address, false);
                        if (delVal.IsNull || !delVal.IsValid) continue;
                        var subs = CollectSubscribers(delVal);
                        if (subs.Count == 0) continue;
                        if (excludes?.Any(e => subs.Any(s => s.TargetType.Contains(e, StringComparison.OrdinalIgnoreCase))) == true)
                            subs = [..subs.Where(s => excludes.All(e => !s.TargetType.Contains(e, StringComparison.OrdinalIgnoreCase)))];
                        if (subs.Count > 0)
                            leaks.Add((typeName, obj.Address, field.Name ?? "<?>", field.Type.Name ?? "?", subs));
                    }
                    catch { }
                }
            }
        });

        // Group by (PublisherType, FieldName)
        var groups = leaks
            .GroupBy(l => (l.PublisherType, l.FieldName))
            .Select(g => (g.Key.PublisherType, g.Key.FieldName, Instances: g.Count(), Subscribers: g.Sum(l => l.Subs.Count)))
            .OrderByDescending(g => g.Subscribers)
            .ToList();

        sink.Section("Event Handler Leaks");

        if (groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No event handler leaks found.");
            return;
        }

        var summaryRows = groups.Select(g => new[]
        {
            g.PublisherType, g.FieldName,
            g.Instances.ToString("N0"),
            g.Subscribers.ToString("N0"),
        }).ToList();
        sink.Table(["Publisher Type", "Event Field", "Instances", "Subscribers"], summaryRows,
            $"{groups.Count} unique event field(s) across {leaks.Count} publisher instance(s)");

        if (showAddr)
        {
            var detailRows = leaks.SelectMany(l =>
                l.Subs.Select(s => new[] { l.PublisherType, $"0x{l.PublisherAddr:X16}", l.FieldName, s.TargetType, $"0x{s.TargetAddr:X16}" })
            ).Take(200).ToList();
            sink.Table(["Publisher Type", "Publisher Addr", "Field", "Subscriber Type", "Subscriber Addr"], detailRows);
        }

        int totalSubs = groups.Sum(g => g.Subscribers);
        if (totalSubs > 1000)
            sink.Alert(AlertLevel.Critical, $"High subscriber count: {totalSubs:N0} total event subscriptions.",
                advice: "Unsubscribe event handlers when the subscriber is disposed (-= handler).");
        else if (totalSubs > 200)
            sink.Alert(AlertLevel.Warning, $"{totalSubs:N0} event subscriptions found.");

        sink.KeyValues([
            ("Unique event fields",  groups.Count.ToString("N0")),
            ("Publisher instances",  leaks.Count.ToString("N0")),
            ("Total subscribers",    totalSubs.ToString("N0")),
        ]);
    }

    static List<(string TargetType, ulong TargetAddr, ulong DelAddr)> CollectSubscribers(ClrObject del)
    {
        var result = new List<(string, ulong, ulong)>();
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
                    var sub = TryGetSub(item);
                    if (sub.HasValue) result.Add(sub.Value);
                }
            }
            else
            {
                var sub = TryGetSub(del);
                if (sub.HasValue) result.Add(sub.Value);
            }
        }
        catch { }
        return result;
    }

    static (string TargetType, ulong TargetAddr, ulong DelAddr)? TryGetSub(ClrObject del)
    {
        var target = del.ReadObjectField("_target");
        if (target.IsNull) return null;
        var targetType = target.Type?.Name ?? "<unknown>";
        if (DumpHelpers.IsSystemType(targetType)) return null;
        return (targetType, target.Address, del.Address);
    }

    static bool IsDelegate(ClrType type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "System.MulticastDelegate" or "System.Delegate") return true;
        return false;
    }
}
