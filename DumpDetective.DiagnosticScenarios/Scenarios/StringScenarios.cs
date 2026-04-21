namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenario for: string-duplicates
// Creates thousands of uninterned copies of a small set of strings so that
// DumpDetective string-duplicates reports high waste from identical string values.
internal static class StringScenarios
{
    private static readonly List<string> _strings = [];

    // 6 high-frequency string templates × 500 copies each = 3 000 heap strings.
    // Using 'new string(...)' prevents the runtime from interning them.
    private static readonly string[] Templates =
    [
        "https://api.contoso.com/v1/orders",
        "SELECT * FROM Orders WHERE CustomerId = @id AND Status = 'Active'",
        "3b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
        @"C:\ProgramData\Contoso\Logs\app-2024.log",
        "Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9",
        "System.NullReferenceException: Object reference not set to an instance of an object.",
    ];

    public static IResult TriggerStringDuplicates()
    {
        const int copiesPerTemplate = 500;
        foreach (var template in Templates)
            for (int i = 0; i < copiesPerTemplate; i++)
                _strings.Add(new string(template.AsSpan())); // forces new heap allocation

        return Results.Ok(new
        {
            message = $"{_strings.Count} duplicate strings ({Templates.Length} unique values × {copiesPerTemplate} copies each).",
            command = "DumpDetective string-duplicates <dump.dmp>",
            templates = Templates.Length,
        });
    }

    public static string Status => $"string-duplicates: {_strings.Count} heap strings";

    public static void Reset() => _strings.Clear();
}
