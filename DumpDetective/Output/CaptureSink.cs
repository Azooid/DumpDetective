using DumpDetective.Core;
using DumpDetective.Models;

namespace DumpDetective.Output;

/// <summary>
/// An <see cref="IRenderSink"/> that captures every call into an in-memory
/// <see cref="ReportDoc"/> without writing to disk.
/// Used by <c>trend-analysis --full --output *.json</c> to capture per-dump
/// sub-report output so it can be replayed later by <c>trend-render</c> or
/// <c>render</c> without reopening dump files.
/// </summary>
internal sealed class CaptureSink : IRenderSink
{
    readonly ReportDoc            _doc          = new();
    ReportChapter?                _chapter;
    ReportSection?                _section;
    readonly Stack<ReportDetails> _detailsStack = new();

    public bool    IsFile   => false;
    public string? FilePath => null;

    /// <summary>Returns the captured document after all sink calls are done.</summary>
    public ReportDoc GetDoc() => _doc;

    // ── helpers ───────────────────────────────────────────────────────────────

    List<ReportElement> CurrentElements()
    {
        if (_detailsStack.TryPeek(out var det))
            return det.Elements;
        EnsureSection();
        return _section!.Elements;
    }

    void EnsureChapter()
    {
        if (_chapter is null)
        {
            _chapter = new ReportChapter { Title = "Report" };
            _doc.Chapters.Add(_chapter);
        }
    }

    void EnsureSection()
    {
        EnsureChapter();
        if (_section is null)
        {
            _section = new ReportSection();
            _chapter!.Sections.Add(_section);
        }
    }

    // ── IRenderSink ───────────────────────────────────────────────────────────

    public void Header(string title, string? subtitle = null)
    {
        _detailsStack.Clear();
        _chapter = new ReportChapter
        {
            Title    = title,
            Subtitle = subtitle,
            NavLevel = CommandBase.SuppressVerbose ? 2 : 1,
        };
        _section = null;
        _doc.Chapters.Add(_chapter);
    }

    public void Section(string title)
    {
        _detailsStack.Clear();
        EnsureChapter();
        _section = new ReportSection { Title = title };
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

    public void BlankLine() { /* not captured — content-free */ }

    public void BeginDetails(string title, bool open = false)
    {
        var det = new ReportDetails { Title = title, Open = open };
        CurrentElements().Add(det);
        _detailsStack.Push(det);
    }

    public void EndDetails()
    {
        if (_detailsStack.Count > 0)
            _detailsStack.Pop();
    }

    public void Dispose() { /* nothing to flush */ }
}
