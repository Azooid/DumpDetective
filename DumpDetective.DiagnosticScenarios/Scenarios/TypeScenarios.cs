namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: type-instances, object-inspect
internal static class TypeScenarios
{
    // ── type-instances ────────────────────────────────────────────────────────
    // 1 000 instances of TargetObject kept alive in a static list.
    // Run: DumpDetective type-instances <dump.dmp> --type TargetObject
    private static readonly List<TargetObject> _instances = [];

    public static IResult TriggerTypeInstances()
    {
        const int count = 1_000;
        for (int i = 0; i < count; i++)
            _instances.Add(new TargetObject(
                id:      i,
                name:    $"target-{i:D4}",
                value:   i * 3.14,
                tags:    [$"tag-{i % 10}", $"group-{i % 5}"],
                payload: new byte[128]));

        return Results.Ok(new
        {
            message = $"{_instances.Count} TargetObject instances on heap.",
            fullTypeName = typeof(TargetObject).FullName,
            command = $"DumpDetective type-instances <dump.dmp> --type TargetObject",
        });
    }

    public static string TypeStatus => $"type-instances: {_instances.Count} TargetObject instances";

    // ── object-inspect ────────────────────────────────────────────────────────
    // One well-known InspectableObject with predictable field values.
    // The endpoint returns the managed object's address so you can pass it to
    // DumpDetective object-inspect --address <addr>.
    private static InspectableObject? _inspectable;

    public static IResult TriggerObjectInspect()
    {
        _inspectable = new InspectableObject(
            orderId:         12345,
            customerName:    "Contoso Ltd",
            status:          "Processing",
            amount:          9_999.99m,
            createdUtc:      new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            tags:            ["urgent", "international", "vip"],
            retryCount:      3,
            correlationId:   Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
            innerException:  new TimeoutException("Upstream payment gateway timed out."));

        // Obtain the managed pointer as a hex string for the hint message.
        // TypedReference / object identity trick that works without unsafe code
        string addrHint = "use 'dotnet-dump analyze <dump.dmp>' then '!dumpheap -type InspectableObject' to find the address";

        return Results.Ok(new
        {
            message = "InspectableObject created with known field values.",
            fields  = new
            {
                orderId        = 12345,
                customerName   = "Contoso Ltd",
                status         = "Processing",
                amount         = 9_999.99,
                createdUtc     = "2024-06-15T10:30:00Z",
                retryCount     = 3,
                correlationId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
                innerException = "System.TimeoutException",
            },
            command = "DumpDetective object-inspect <dump.dmp> --address <addr>",
            hint    = addrHint,
        });
    }

    public static string InspectStatus => $"object-inspect: {(_inspectable is null ? "not created" : "active")}";

    public static void Reset()
    {
        _instances.Clear();
        _inspectable = null;
    }

    // ── Custom types ──────────────────────────────────────────────────────────

    // Named type that type-instances will search for
    public sealed class TargetObject(int id, string name, double value, string[] tags, byte[] payload)
    {
        public int    Id      = id;
        public string Name    = name;
        public double Value   = value;
        public string[] Tags  = tags;
        public byte[] Payload = payload;
    }

    // Type with rich fields for object-inspect inspection
    public sealed class InspectableObject(
        int orderId, string customerName, string status, decimal amount,
        DateTime createdUtc, string[] tags, int retryCount,
        Guid correlationId, Exception? innerException)
    {
        public int       OrderId        = orderId;
        public string    CustomerName   = customerName;
        public string    Status         = status;
        public decimal   Amount         = amount;
        public DateTime  CreatedUtc     = createdUtc;
        public string[]  Tags           = tags;
        public int       RetryCount     = retryCount;
        public Guid      CorrelationId  = correlationId;
        public Exception? InnerException = innerException;
    }
}
