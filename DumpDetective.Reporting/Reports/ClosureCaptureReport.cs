using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class ClosureCaptureReport
{
    public void Render(ClosureCaptureData data, IRenderSink sink)
    {
        sink.Section("Closure Capture Summary");
        sink.Explain(
            what: "Compiler-generated closure display-class objects (\u003c\u003ec__DisplayClass*) are allocated " +
                  "whenever a lambda or anonymous method captures variables from the enclosing scope. " +
                  "Each captured variable holds a live reference — if the closure escapes to a long-lived context, " +
                  "everything it captured (HttpContext, DbContext, large arrays) is kept alive.",
            why:  "Closures that escape LINQ queries, event subscriptions, async callbacks, or thread pool work items " +
                  "can silently retain large object graphs. A high retained-to-own-size ratio indicates the closure " +
                  "is holding references to objects far larger than the closure itself.",
            bullets:
            [
                "High retained size → closure is capturing a large graph (HTTP context, DI scope, buffer)",
                "Many instances of the same closure → fire-and-forget callbacks accumulating without completion",
                "Closures from service types → possible DI scope leak (scoped service captured in singleton lambda)",
                "Closures in timer callbacks → long-lived capture; ensure timer is disposed",
            ],
            action: "Identify the top retaining closures. Check captured fields. " +
                    "Avoid capturing 'this' in long-lived lambdas — extract only the needed value.",
            impact: "Each closure instance holds its captured fields alive for as long as the delegate exists. " +
                    "Thousands of closure instances from async I/O callbacks can retain hundreds of MB of request state.");

        sink.KeyValues([
            ("Total closure instances", data.TotalClosures.ToString("N0")),
            ("Total own size",          DumpHelpers.FormatSize(data.TotalOwnSize)),
            ("Total retained size",     DumpHelpers.FormatSize(data.TotalRetainedSize)),
        ]);

        if (data.TotalRetainedSize > data.TotalOwnSize * 5)
            sink.Alert(AlertLevel.Warning,
                $"Closures retain {DumpHelpers.FormatSize(data.TotalRetainedSize)} — " +
                $"{data.TotalRetainedSize / Math.Max(1, data.TotalOwnSize):N0}× their own size.",
                "Closures are holding large external graphs alive. Review captured fields for HttpContext, DbContext, or large buffers.");

        if (data.Groups.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No compiler-generated closure display-class objects found.");
            return;
        }

        sink.Section("Closure Groups by Retained Size");
        var rows = data.Groups
            .OrderByDescending(g => g.RetainedSizeTotal)
            .Select(g =>
            {
                string decl = g.DeclaringType.Length > 55
                    ? "\u2026" + g.DeclaringType[^54..] : g.DeclaringType;
                string closure = g.ClosureTypeName.Length > 40
                    ? g.ClosureTypeName[..40] + "\u2026" : g.ClosureTypeName;
                return new[]
                {
                    decl,
                    closure,
                    g.Count.ToString("N0"),
                    DumpHelpers.FormatSize(g.OwnSizeTotal),
                    g.RetainedSizeTotal > 0
                        ? DumpHelpers.FormatSize(g.RetainedSizeTotal) + (g.IsEstimated ? " ~" : "")
                        : "\u2014",
                };
            })
            .ToList();

        bool hasRetained = data.Groups.Any(g => g.RetainedSizeTotal > 0);
        sink.Table(
            hasRetained
                ? ["Declaring Type", "Closure Type", "Count", "Own Size", "Retained Size"]
                : ["Declaring Type", "Closure Type", "Count", "Own Size"],
            rows,
            hasRetained
                ? "Retained size via BFS from sample instances. '~' = estimated (sampled). \u2014 = BFS index not available."
                : "Run 'load <dump>' first to build the BFS index for retained size computation.");

        // Expanded details for top 5 groups with captured fields
        var withFields = data.Groups.Where(g => g.CapturedFields.Count > 0).Take(5).ToList();
        if (withFields.Count > 0)
        {
            sink.Section("Captured Field Details");
            foreach (var g in withFields)
            {
                bool open = g == withFields[0];
                sink.BeginDetails(
                    $"{(g.DeclaringType.Length > 60 ? "\u2026" + g.DeclaringType[^59..] : g.DeclaringType)}  " +
                    $"({g.Count:N0} instances  /  {DumpHelpers.FormatSize(g.OwnSizeTotal)})",
                    open: open);
                sink.Table(
                    ["Captured Field", "Type"],
                    g.CapturedFields.Select(f =>
                    {
                        var parts = f.Split(':', 2);
                        return new[] { parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "?" };
                    }).ToList());
                sink.EndDetails();
            }
        }
    }
}
