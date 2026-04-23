using System.Text.Json.Serialization;

namespace DumpDetective.Core.Models;

// ── Document model ────────────────────────────────────────────────────────────

/// <summary>
/// A structured, serialisable capture of everything passed to an
/// <c>IRenderSink</c> — chapters, sections, tables, alerts, etc.
/// Produced by <c>CaptureSink</c>; replayed by <c>ReportDocReplay</c>;
/// stored in trend-raw JSON for offline rendering.
/// </summary>
public sealed class ReportDoc
{
    public List<ReportChapter> Chapters { get; set; } = [];
}

public sealed class ReportChapter
{
    public string  Title    { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public int     NavLevel { get; set; } = 1;
    /// <summary>
    /// CLI command name that produced this chapter (e.g. "memory-leak").
    /// Set automatically by <c>AnalyzeReport.RenderEmbeddedReports</c> after
    /// <c>BuildReport</c> returns. Used by <c>render --command</c> to slice
    /// a <see cref="ReportDoc"/> without title-string matching.
    /// Null on chapters not produced by a registered command (e.g. the analyze summary).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandName { get; set; }
    public List<ReportSection> Sections { get; set; } = [];
}

public sealed class ReportSection
{
    public string? Title      { get; set; }
    public string? SectionKey { get; set; }
    public List<ReportElement> Elements { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReportKeyValues), "keyValues")]
[JsonDerivedType(typeof(ReportTable),     "table")]
[JsonDerivedType(typeof(ReportAlert),     "alert")]
[JsonDerivedType(typeof(ReportText),      "text")]
[JsonDerivedType(typeof(ReportDetails),   "details")]
[JsonDerivedType(typeof(ReportExplain),   "explain")]
public abstract class ReportElement { }

public sealed class ReportKeyValues : ReportElement
{
    public string?          Title { get; set; }
    public List<ReportPair> Pairs { get; set; } = [];
}

public sealed class ReportPair
{
    public string Key   { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ReportPair() { }
    public ReportPair(string key, string value) { Key = key; Value = value; }
}

public sealed class ReportTable : ReportElement
{
    public string?        Caption { get; set; }
    public string[]       Headers { get; set; } = [];
    public List<string[]> Rows    { get; set; } = [];
}

public sealed class ReportAlert : ReportElement
{
    public string  Level  { get; set; } = "info";
    public string  Title  { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Advice { get; set; }
}

public sealed class ReportText : ReportElement
{
    public string Content { get; set; } = string.Empty;
}

public sealed class ReportDetails : ReportElement
{
    public string  Title    { get; set; } = string.Empty;
    public bool    Open     { get; set; }
    public List<ReportElement> Elements { get; set; } = [];
}

/// <summary>
/// Structured explanation block rendered in HTML as an information card.
/// Answers: What / Why / Impact / What-to-look-for / Recommended action.
/// In non-HTML sinks rendered as inline text paragraphs.
/// </summary>
public sealed class ReportExplain : ReportElement
{
    public string?   What    { get; set; }
    public string?   Why     { get; set; }
    public string?   Impact  { get; set; }
    public string[]? Bullets { get; set; }
    public string?   Action  { get; set; }
}

// ── Top-level JSON envelope ───────────────────────────────────────────────────

/// <summary>
/// Written by <c>JsonSink</c> when output is a <c>.json</c> file.
/// Readable by <c>DumpDetective render report.json</c>.
/// </summary>
public sealed class DumpReportEnvelope
{
    public string    Format      { get; set; } = "report";
    public string    GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string    Title       { get; set; } = string.Empty;
    public string?   Subtitle    { get; set; }
    public ReportDoc Doc         { get; set; } = new();
}
