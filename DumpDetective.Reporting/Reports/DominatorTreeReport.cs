using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class DominatorTreeReport
{
    public void Render(DominatorTreeData data, IRenderSink sink)
    {
        sink.Section("Summary", "dominator-tree-summary");
        sink.Explain(
            what: "The dominator tree shows which objects exclusively retain the most heap memory. " +
                  "Object A dominates object B when every path from the GC roots to B passes through A — " +
                  "freeing A would allow B (and the entire subtree beneath it) to be collected.",
            why:  "Unlike simple retained-size analysis, the dominator tree is provably accurate: " +
                  "it uses the Lengauer-Tarjan algorithm over the full reference graph (not a BFS spanning tree). " +
                  "Each node's retained size is the exact memory freed by eliminating that single object.",
            bullets:
            [
                "Top-level nodes (depth 1) are dominated directly by the GC root set — leaking these is catastrophic",
                "A type with high retained size but low shallow size = it holds references to large object graphs",
                "Instance count > 1 at the same level means multiple instances of that type dominate similar subgraphs",
            ],
            action: "Focus on the top-level nodes by retained size. Each is a self-contained leak target.");

        sink.KeyValues(
        [
            ("Total heap",         DumpHelpers.FormatSize(data.TotalHeapBytes)),
            ("Reachable objects",  $"{data.TotalReachableObjects:N0}"),
            ("Tree display depth", data.DisplayDepth.ToString()),
            ("Results truncated",  data.IsTruncated ? "Yes (increase --top to see more)" : "No"),
        ], title: "Dominator Tree Stats");

        sink.Section("Dominator Tree", "dominator-tree");

        if (data.Roots.Count == 0)
        {
            sink.Alert(AlertLevel.Info, "No qualifying roots",
                "All top-level dominators are below the --min-size threshold. " +
                "Lower --min-size or check that the BFS index is fresh.");
            return;
        }

        sink.DomTree(data.Roots, data.TotalHeapBytes,
            caption: "Retained = exact bytes freed by removing this object group  •  Shallow = own bytes  •  % = fraction of total heap",
            topN: data.Roots.Count);
    }
}
