using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Sinks;

/// <summary>
/// An <see cref="IRenderSink"/> that captures every call into an in-memory
/// <see cref="ReportDoc"/> without writing to disk.
/// Used for parallel sub-report buffering in <c>AnalyzeCommand</c> and by
/// <c>JsonSink</c> for serialisation.
/// </summary>
public sealed class CaptureSink : IRenderSink
{
    private readonly ReportDoc            _doc          = new();
    private          ReportChapter?        _chapter;
    private          ReportSection?        _section;
    private readonly Stack<ReportDetails>  _detailsStack = new();

    public bool    IsFile   => false;
    public string? FilePath => null;

    public ReportDoc GetDoc() => _doc;

    private List<ReportElement> CurrentElements()
    {
        if (_detailsStack.TryPeek(out var det)) return det.Elements;
        EnsureSection();
        return _section!.Elements;
    }

    private void EnsureChapter()
    {
        if (_chapter is null)
        {
            _chapter = new ReportChapter { Title = "Report" };
            _doc.Chapters.Add(_chapter);
        }
    }

    private void EnsureSection()
    {
        EnsureChapter();
        if (_section is null)
        {
            _section = new ReportSection();
            _chapter!.Sections.Add(_section);
        }
    }

    public void Header(string title, string? subtitle = null, int navLevel = 0, string? commandName = null)
    {
        _detailsStack.Clear();
        _chapter = new ReportChapter
        {
            Title       = title,
            Subtitle    = subtitle,
            CommandName = commandName,
            NavLevel    = navLevel > 0 ? navLevel :
                CommandBase.SuppressVerbose
                    ? (title.StartsWith("Per-Dump Report", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    : 1,
        };
        _section = null;
        _doc.Chapters.Add(_chapter);
    }

    public void Section(string title, string? sectionKey = null)
    {
        _detailsStack.Clear();
        EnsureChapter();
        _section = new ReportSection { Title = title, SectionKey = sectionKey };
        _chapter!.Sections.Add(_section);
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
        => CurrentElements().Add(new ReportKeyValues
        {
            Title = title,
            Pairs = [.. pairs.Select(p => new ReportPair(p.Key, p.Value))],
        });

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
        => CurrentElements().Add(new ReportTable
        {
            Caption = caption,
            Headers = headers,
            Rows    = [.. rows],
        });

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
        => CurrentElements().Add(new ReportAlert
        {
            Level  = level switch { AlertLevel.Critical => "critical", AlertLevel.Warning => "warning", _ => "info" },
            Title  = title,
            Detail = detail,
            Advice = advice,
        });

    public void Text(string line)
        => CurrentElements().Add(new ReportText { Content = line });

    public void Reference(string label, string url)
        => CurrentElements().Add(new ReportReference { Label = label, Url = url });

    public void BlankLine() { /* not captured — content-free */ }

    public void BeginDetails(string title, bool open = false)
    {
        var det = new ReportDetails { Title = title, Open = open };
        CurrentElements().Add(det);
        _detailsStack.Push(det);
    }

    public void EndDetails()
    {
        if (_detailsStack.Count > 0) _detailsStack.Pop();
    }

    public void CallTree(IReadOnlyList<DumpDetective.Core.Models.CallTreeNode> roots,
                         string? caption = null, int topN = 20)
    {
        static ReportCallTreeNode ToDto(CallTreeNode n) => new()
        {
            Method           = n.Method,
            Module           = n.Module,
            InclusiveSamples = n.InclusiveSamples,
            ExclusiveSamples = n.ExclusiveSamples,
            InclusivePct     = n.InclusivePct,
            ExclusivePct     = n.ExclusivePct,
            Children         = [.. n.Children.Select(ToDto)],
        };

        CurrentElements().Add(new ReportCallTree
        {
            Roots   = [.. roots.Take(topN).Select(ToDto)],
            Caption = caption,
            TopN    = topN,
        });
    }

    public void Explain(string? what, string? why = null, string[]? bullets = null,
                        string? impact = null, string? action = null)
        => CurrentElements().Add(new ReportExplain
        {
            What    = what,
            Why     = why,
            Impact  = impact,
            Bullets = bullets,
            Action  = action,
        });

    public void Gauges(IReadOnlyList<(string Label, double Value, string Unit)> items, double barMax = 100.0)
        => CurrentElements().Add(new ReportGauges
        {
            Items  = [.. items.Select(i => new ReportGaugeItem(i.Label, i.Value, i.Unit))],
            BarMax = barMax,
        });

    public void DonutChart(IReadOnlyList<(string Label, double Value)> segments,
        string? caption = null, string? centerText = null)
        => CurrentElements().Add(new ReportDonutChart
        {
            Segments   = [.. segments.Select(s => new ReportChartSeg(s.Label, s.Value))],
            Caption    = caption,
            CenterText = centerText,
        });

    public void StackedBar(IReadOnlyList<(string Label, double Value)> segments,
        string? unit = null, string? caption = null, string? valueMode = null)
        => CurrentElements().Add(new ReportStackedBar
        {
            Segments  = [.. segments.Select(s => new ReportChartSeg(s.Label, s.Value))],
            Unit      = unit,
            Caption   = caption,
            ValueMode = valueMode,
        });

    public void Sparkline(IReadOnlyList<double> values, string? caption = null, string? unit = null,
        string? valueMode = null)
        => CurrentElements().Add(new ReportSparkline
        {
            Values    = [.. values],
            Caption   = caption,
            Unit      = unit,
            ValueMode = valueMode,
        });

    public void Dispose() { }
}
