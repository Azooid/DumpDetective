using System.Text.Json.Serialization;

namespace DumpDetective.Models;

// ── Document model ────────────────────────────────────────────────────────────

/// <summary>
/// A structured, serialisable capture of everything passed to an
/// <c>IRenderSink</c> — chapters, sections, tables, alerts, etc.
/// Produced live by <c>CaptureSink</c> and written into the raw trend JSON
/// so that <c>trend-render</c> (or the <c>render</c> command) can replay it
/// through any sink (HTML, Markdown, text, console) without reopening dump files.
/// </summary>
public sealed class ReportDoc
{
    public List<ReportChapter> Chapters { get; set; } = [];
}

public sealed class ReportChapter
{
    public string  Title    { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    /// <summary>1 = top-level chapter, 2 = sub-chapter (SuppressVerbose was on when Header() was called).</summary>
    public int     NavLevel { get; set; } = 1;
    public List<ReportSection> Sections { get; set; } = [];
}

public sealed class ReportSection
{
    public string? Title    { get; set; }
    public List<ReportElement> Elements { get; set; } = [];
}

/// <summary>
/// Polymorphic base for all captured sink calls.
/// The <c>"type"</c> discriminator is serialised by STJ so round-trips are lossless.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReportKeyValues), "keyValues")]
[JsonDerivedType(typeof(ReportTable),     "table")]
[JsonDerivedType(typeof(ReportAlert),     "alert")]
[JsonDerivedType(typeof(ReportText),      "text")]
[JsonDerivedType(typeof(ReportDetails),   "details")]
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
    /// <summary>"info" | "warning" | "critical"</summary>
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

// ── Top-level envelope written by JsonSink ────────────────────────────────────

/// <summary>
/// Top-level JSON envelope written when any command is run with <c>--output report.json</c>.
/// Readable by <c>DumpDetective render report.json --output report.html</c>.
/// </summary>
public sealed class DumpReportEnvelope
{
    /// <summary>Always "report" — distinguishes this from a "trend-raw" export.</summary>
    public string    Format      { get; set; } = "report";
    public string    GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string    Title       { get; set; } = string.Empty;
    public string?   Subtitle    { get; set; }
    public ReportDoc Doc         { get; set; } = new();
}
