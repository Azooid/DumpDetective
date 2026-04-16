using DumpDetective.Models;
using DumpDetective.Output;

namespace DumpDetective.Helpers;

/// <summary>
/// Replays a captured <see cref="ReportDoc"/> through any <see cref="IRenderSink"/>,
/// reproducing the exact output the original command produced when the data was captured.
/// </summary>
internal static class ReportDocReplay
{
    public static void Replay(ReportDoc doc, IRenderSink sink)
    {
        foreach (var chapter in doc.Chapters)
        {
            sink.Header(chapter.Title, chapter.Subtitle, chapter.NavLevel);
            foreach (var section in chapter.Sections)
            {
                if (section.Title is not null)
                    sink.Section(section.Title);
                ReplayElements(section.Elements, sink);
            }
        }
    }

    static void ReplayElements(List<ReportElement> elements, IRenderSink sink)
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
            }
        }
    }
}
