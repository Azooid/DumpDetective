using Spectre.Console;

namespace DumpDetective.Output;

/// <summary>Rich terminal output via Spectre.Console.</summary>
internal sealed class ConsoleSink : IRenderSink
{
    public bool    IsFile   => false;
    public string? FilePath => null;

    public void Header(string title, string? subtitle = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold deepskyblue1] {Markup.Escape(title)} [/]").LeftJustified());
        if (subtitle is not null)
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(subtitle)}[/]");
        AnsiConsole.WriteLine();
    }

    public void Section(string title)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
    }

    public void KeyValues(IReadOnlyList<(string Key, string Value)> pairs, string? title = null)
    {
        if (title is not null)
            AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(title)}[/]");
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(2))
            .AddColumn();
        foreach (var (k, v) in pairs)
            grid.AddRow($"[dim]{Markup.Escape(k)}[/]", Markup.Escape(v));
        AnsiConsole.Write(grid);
    }

    public void Table(string[] headers, IReadOnlyList<string[]> rows, string? caption = null)
    {
        if (rows.Count == 0) { AnsiConsole.MarkupLine("[dim]  (no data)[/]"); return; }
        if (caption is not null) AnsiConsole.MarkupLine($"[dim]{Markup.Escape(caption)}[/]");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        foreach (var h in headers)
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(h)}[/]"));
        foreach (var row in rows)
        {
            var cells = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
                cells[i] = i < row.Length ? Markup.Escape(row[i]) : string.Empty;
            table.AddRow(cells);
        }
        AnsiConsole.Write(table);
    }

    public void Alert(AlertLevel level, string title, string? detail = null, string? advice = null)
    {
        var (color, icon) = level switch
        {
            AlertLevel.Critical => (Color.Red,    "✗"),
            AlertLevel.Warning  => (Color.Yellow, "⚠"),
            _                   => (Color.Blue,   "ℹ"),
        };
        var sb = new System.Text.StringBuilder(Markup.Escape(title));
        if (detail is not null) sb.Append($"\n[dim]{Markup.Escape(detail)}[/]");
        if (advice  is not null) sb.Append($"\n[green]→ {Markup.Escape(advice)}[/]");

        var panel = new Panel(sb.ToString())
            .BorderColor(color);
        panel.Header = new PanelHeader($"[bold]{icon} {level}[/]");
        AnsiConsole.Write(panel);
    }

    public void Text(string line)  => AnsiConsole.MarkupLine(Markup.Escape(line));
    public void BlankLine()        => AnsiConsole.WriteLine();
    public void Dispose()          { }
}
