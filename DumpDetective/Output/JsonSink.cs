using DumpDetective.Core;
using System.Text.Json;

namespace DumpDetective.Output;

/// <summary>
/// Serialises the report to a structured JSON file so it can be converted to HTML
/// (or any other format) at any later time without re-running the analysis.
///
/// Top-level shape:
/// {
///   "generatedAt": "ISO-8601",
///   "title":       "...",
///   "subtitle":    "...",          // present when the first Header() call has a subtitle
///   "chapters": [
///     {
///       "title":    "...",
///       "subtitle": "...",         // pipe-separated chips from Header()
///       "sections": [
///         {
///           "title":    "...",     // null for the implicit root section
///           "elements": [
///             { "type": "keyValues", "title": "...", "pairs": [{"key":"K","value":"V"}] },
///             { "type": "table",  "caption":"...", "headers":["H1"], "rows":[["v1"]] },
///             { "type": "alert",  "level":"info|warning|critical", "title":"...", "detail":"...", "advice":"..." },
///             { "type": "text",   "content":"..." },
///             { "type": "details","title":"...","open":true, "elements":[...] }
///           ]
///         }
///       ]
///     }
///   ]
/// }
/// </summary>
internal sealed class JsonSink : IRenderSink
{
    // ── in-memory DOM ─────────────────────────────────────────────────────────

    sealed class JDoc
    {
        public string           GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string           Title       { get; set; } = string.Empty;
        public string?          Subtitle    { get; set; }
        public List<JChapter>   Chapters    { get; }      = [];
    }

    sealed class JChapter
    {
        public string          Title    { get; set; } = string.Empty;
        public string?         Subtitle { get; set; }
        public int             NavLevel { get; set; } = 1;
        public List<JSection>  Sections { get; }      = [];
    }

    sealed class JSection
    {
        public string?        Title    { get; set; }
        public List<JElem>    Elements { get; }      = [];
    }

    abstract class JElem { public abstract string Type { get; } }

    sealed class JKeyValues(string? title, List<(string Key, string Value)> pairs) : JElem
    {
        public override string Type  => "keyValues";
        public string?         Title => title;
        public List<(string Key, string Value)> Pairs => pairs;
    }

    sealed class JTable(string? caption, string[] headers, List<string[]> rows) : JElem
    {
        public override string   Type    => "table";
        public string?           Caption => caption;
        public string[]          Headers => headers;
        public List<string[]>    Rows    => rows;
    }

    sealed class JAlert(AlertLevel level, string title, string? detail, string? advice) : JElem
    {
        public override string Type   => "alert";
        public string          Level  => level.ToString().ToLowerInvariant();
        public string          Title  => title;
        public string?         Detail => detail;
        public string?         Advice => advice;
    }

    sealed class JText(string content) : JElem
    {
        public override string Type    => "text";
        public string          Content => content;
    }

    sealed class JDetails(string title, bool open) : JElem
    {
        public override string  Type     => "details";
        public string           Title    => title;
        public bool             Open     => open;
        public List<JElem>      Elements { get; } = [];
    }

    // ── state ─────────────────────────────────────────────────────────────────

    readonly string  _path;
    readonly JDoc    _doc  = new();
    JChapter?        _chapter;
    JSection?        _section;
    readonly Stack<JDetails> _detailsStack = new();

    public bool    IsFile   => true;
    public string? FilePath => _path;

    public JsonSink(string path)
    {
        _path = path;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the element list that new elements should be appended to.</summary>
    List<JElem> CurrentElements()
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
            _chapter = new JChapter { Title = "Report" };
            _doc.Chapters.Add(_chapter);
            if (_doc.Title.Length == 0) _doc.Title = "Dump Detective";
        }
    }

    void EnsureSection()
    {
        EnsureChapter();
        if (_section is null)
        {
            _section = new JSection();
            _chapter!.Sections.Add(_section);
        }
    }

    // ── IRenderSink ───────────────────────────────────────────────────────────

    public void Header(string title, string? subtitle = null)
    {
        _detailsStack.Clear();
        _chapter  = new JChapter { Title = title, Subtitle = subtitle, NavLevel = CommandBase.SuppressVerbose ? 2 : 1 };
        _section  = null;
        _doc.Chapters.Add(_chapter);

        // Use the first header as the document title
        if (_doc.Title.Length == 0)
        {
            _doc.Title    = title;
            _doc.Subtitle = subtitle;
        }
    }

    public void Section(string title)
    {
        _detailsStack.Clear();
        EnsureChapter();
        _section = new JSection { Title = title };
        _chapter!.Sections.Add(_section);
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        CurrentElements().Add(new JKeyValues(title, [.. pairs]));
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        CurrentElements().Add(new JTable(caption, headers, [.. rows]));
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        CurrentElements().Add(new JAlert(level, title, detail, advice));
    }

    public void Text(string line)
    {
        CurrentElements().Add(new JText(line));
    }

    public void BlankLine() { /* omitted – not meaningful in structured JSON */ }

    public void BeginDetails(string title, bool open = false)
    {
        var det = new JDetails(title, open);
        CurrentElements().Add(det);   // attach to current scope first …
        _detailsStack.Push(det);      // … then push so children go inside it
    }

    public void EndDetails()
    {
        if (_detailsStack.Count > 0)
            _detailsStack.Pop();
    }

    // ── serialisation ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        using var fs      = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        var       opts    = new JsonWriterOptions { Indented = true };
        using var writer  = new Utf8JsonWriter(fs, opts);

        WriteDoc(writer, _doc);
        writer.Flush();
    }

    // ── Utf8JsonWriter helpers (reflection-free, AOT-safe) ────────────────────

    static void WriteDoc(Utf8JsonWriter w, JDoc doc)
    {
        w.WriteStartObject();
        w.WriteString("generatedAt", doc.GeneratedAt);
        w.WriteString("title",       doc.Title);
        WriteStringOrNull(w, "subtitle", doc.Subtitle);
        w.WriteStartArray("chapters");
        foreach (var ch in doc.Chapters) WriteChapter(w, ch);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteChapter(Utf8JsonWriter w, JChapter ch)
    {
        w.WriteStartObject();
        w.WriteString("title", ch.Title);
        WriteStringOrNull(w, "subtitle", ch.Subtitle);
        w.WriteNumber("navLevel", ch.NavLevel);
        w.WriteStartArray("sections");
        foreach (var s in ch.Sections) WriteSection(w, s);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteSection(Utf8JsonWriter w, JSection s)
    {
        w.WriteStartObject();
        WriteStringOrNull(w, "title", s.Title);
        w.WriteStartArray("elements");
        foreach (var e in s.Elements) WriteElem(w, e);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteElem(Utf8JsonWriter w, JElem e)
    {
        switch (e)
        {
            case JKeyValues kv:
                w.WriteStartObject();
                w.WriteString("type", kv.Type);
                WriteStringOrNull(w, "title", kv.Title);
                w.WriteStartArray("pairs");
                foreach (var (k, v) in kv.Pairs)
                {
                    w.WriteStartObject();
                    w.WriteString("key",   k);
                    w.WriteString("value", v);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;

            case JTable tbl:
                w.WriteStartObject();
                w.WriteString("type", tbl.Type);
                WriteStringOrNull(w, "caption", tbl.Caption);
                w.WriteStartArray("headers");
                foreach (var h in tbl.Headers) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (var row in tbl.Rows)
                {
                    w.WriteStartArray();
                    foreach (var cell in row) w.WriteStringValue(cell);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;

            case JAlert al:
                w.WriteStartObject();
                w.WriteString("type",  al.Type);
                w.WriteString("level", al.Level);
                w.WriteString("title", al.Title);
                WriteStringOrNull(w, "detail", al.Detail);
                WriteStringOrNull(w, "advice", al.Advice);
                w.WriteEndObject();
                break;

            case JText tx:
                w.WriteStartObject();
                w.WriteString("type",    tx.Type);
                w.WriteString("content", tx.Content);
                w.WriteEndObject();
                break;

            case JDetails det:
                w.WriteStartObject();
                w.WriteString("type",  det.Type);
                w.WriteString("title", det.Title);
                w.WriteBoolean("open", det.Open);
                w.WriteStartArray("elements");
                foreach (var child in det.Elements) WriteElem(w, child);
                w.WriteEndArray();
                w.WriteEndObject();
                break;
        }
    }

    static void WriteStringOrNull(Utf8JsonWriter w, string propertyName, string? value)
    {
        if (value is not null)
            w.WriteString(propertyName, value);
        else
            w.WriteNull(propertyName);
    }
}
