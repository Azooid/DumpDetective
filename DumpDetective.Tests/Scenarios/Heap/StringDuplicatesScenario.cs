using DumpDetective.Core.Models;
using DumpDetective.Tests.Fixtures;

namespace DumpDetective.Tests.Scenarios.Heap;

public sealed class StringDuplicatesScenario : IScenario
{
    private static readonly List<string> _strings = [];

    private static readonly string[] _templates =
    [
        "https://api.contoso.com/v1/orders",
        "SELECT * FROM Orders WHERE CustomerId = @id",
        "3b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
        "Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9",
    ];

    public string CommandName => "string-duplicates";
    public string Description => "4 string templates × 200 heap copies each (800 total).";

    public void Setup()
    {
        const int copies = 200;
        foreach (var t in _templates)
            for (int i = 0; i < copies; i++)
                _strings.Add(new string(t.AsSpan())); // forces new allocation (no interning)
    }

    public void Validate(ReportDoc doc)
    {
        // Summary section always rendered
        DocAssert.HasSection(doc, "Summary");

        // Main groups table: [Count, Wasted, Total, Length, Pattern, Value]
        // We planted 4 templates × 200 copies = 4 rows minimum
        DocAssert.TableByHeadersHasMinRows(doc, 4, "string duplicate groups table",
            "Count", "Wasted", "Total", "Length", "Pattern", "Value");

        // Each group has 200 copies — at least one row must show Count ≥ 200
        DocAssert.FindTable(doc,
            rows => rows.Any(r =>
                r.Length > 0 && long.TryParse(r[0].Replace(",", ""), out long v) && v >= 200),
            "at least one duplicate group with ≥ 200 copies (we planted 200 per template)");

        // One of our templates contains "contoso" — verify it's detected
        DocAssert.AnyTableContainsText(doc, "contoso",
            "our planted 'api.contoso.com' URL strings must appear");
    }
}
