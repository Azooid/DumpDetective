using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting;

/// <summary>
/// Replays a captured <see cref="ReportDoc"/> through any <see cref="IRenderSink"/>,
/// reproducing the original command output without reopening the dump file.
/// Used by <c>trend-render</c>, <c>render</c>, and full-analyze parallel replay.
/// </summary>
public static class ReportDocReplay
{
    public static void Replay(ReportDoc doc, IRenderSink sink)
    {
        foreach (var chapter in doc.Chapters)
        {
            CommandBase.CurrentPluginName = chapter.PluginName;
            sink.Header(chapter.Title, chapter.Subtitle, chapter.NavLevel, chapter.CommandName);
            foreach (var section in chapter.Sections)
            {
                if (section.Title is not null)
                    sink.Section(section.Title, section.SectionKey);
                ReplayElements(section.Elements, sink);
            }
            CommandBase.CurrentPluginName = null;
        }
    }

    private static void ReplayElements(List<ReportElement> elements, IRenderSink sink)
    {
        foreach (var elem in elements)
        {
            switch (elem)
            {
                case ReportKeyValues kv:
                    sink.KeyValues(kv.Pairs.Select(p => (p.Key, p.Value)).ToArray(), kv.Title);
                    break;
                case ReportTable tbl:
                    sink.Table(tbl.Headers, tbl.Rows, tbl.Caption);
                    break;
                case ReportAlert al:
                    var level = al.Level switch
                    {
                        "critical" => AlertLevel.Critical,
                        "warning"  => AlertLevel.Warning,
                        _          => AlertLevel.Info,
                    };
                    sink.Alert(level, al.Title, al.Detail, al.Advice);
                    break;
                case ReportText tx:
                    sink.Text(tx.Content);
                    break;
                case ReportDetails det:
                    sink.BeginDetails(det.Title, det.Open);
                    ReplayElements(det.Elements, sink);
                    sink.EndDetails();
                    break;
                case ReportExplain ex:
                    sink.Explain(ex.What, ex.Why, ex.Bullets, ex.Impact, ex.Action);
                    break;
                case ReportGauges g:
                    sink.Gauges(
                        g.Items.Select(i => (i.Label, i.Value, i.Unit)).ToArray(),
                        g.BarMax);
                    break;
                case ReportDonutChart dc:
                    sink.DonutChart(
                        dc.Segments.Select(s => (s.Label, s.Value)).ToArray(),
                        dc.Caption, dc.CenterText);
                    break;
                case ReportStackedBar sb:
                    sink.StackedBar(
                        sb.Segments.Select(s => (s.Label, s.Value)).ToArray(),
                        sb.Unit, sb.Caption, sb.ValueMode);
                    break;
                case ReportSparkline sp:
                    sink.Sparkline([.. sp.Values], sp.Caption, sp.Unit, sp.ValueMode);
                    break;
                case ReportMultiSparkline ms:
                    sink.MultiSparkline(
                        ms.Series.Select(s =>
                            (s.Label, (IReadOnlyList<double>)[.. s.Values], s.Unit)).ToArray(),
                        ms.Caption, ms.ValueMode);
                    break;
                case ReportCompareBar cb:
                    sink.CompareBar(
                        cb.Items.Select(i => (i.Label, i.ValueA, i.ValueB)).ToArray(),
                        cb.LabelA, cb.LabelB, cb.Unit, cb.Caption, cb.ValueMode);
                    break;
                case ReportCallTree ct:
                    static CallTreeNode ToNode(ReportCallTreeNode n) =>
                        new(n.Method, n.Module, n.InclusiveSamples, n.ExclusiveSamples,
                            n.InclusivePct, n.ExclusivePct,
                            n.Children.Select(ToNode).ToArray());
                    sink.CallTree([.. ct.Roots.Select(ToNode)], ct.Caption, ct.TopN);
                    break;
                case ReportReference rf:
                    sink.Reference(rf.Label, rf.Url);
                    break;
            }
        }
    }
}
