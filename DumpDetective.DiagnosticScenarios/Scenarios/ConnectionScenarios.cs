namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: connection-pool, wcf-channels
// Uses stub types in System.Data.SqlClient and System.ServiceModel namespaces
// so that ClrMD reports the correct fully-qualified type names that DumpDetective
// searches for during its heap walk.
internal static class ConnectionScenarios
{
    // ── connection-pool ───────────────────────────────────────────────────────
    // 100 System.Data.SqlClient.SqlConnection instances never disposed.
    // Mix of Open / Connecting / Fetching states to trigger pool-exhaustion alerts.
    private static readonly List<System.Data.SqlClient.SqlConnection> _connections = [];

    public static IResult TriggerConnectionPool()
    {
        const string cs = "Server=sql01.contoso.com;Database=Orders;User Id=app_svc;Password=***;";

        for (int i = 0; i < 60; i++)
            _connections.Add(new System.Data.SqlClient.SqlConnection(cs, "sql01.contoso.com", "Orders", state: 1 /* Open */));

        for (int i = 0; i < 25; i++)
            _connections.Add(new System.Data.SqlClient.SqlConnection(cs, "sql01.contoso.com", "Orders", state: 4 /* Executing */));

        for (int i = 0; i < 15; i++)
            _connections.Add(new System.Data.SqlClient.SqlConnection(cs, "sql01.contoso.com", "Orders", state: 2 /* Connecting */));

        return Results.Ok(new
        {
            message = $"{_connections.Count} System.Data.SqlClient.SqlConnection objects on heap (undisposed).",
            breakdown = new { open = 60, executing = 25, connecting = 15 },
            command = "DumpDetective connection-pool <dump.dmp>",
        });
    }

    public static string ConnectionStatus => $"connection-pool: {_connections.Count} connections";

    // ── wcf-channels ──────────────────────────────────────────────────────────
    // 50 System.ServiceModel.ClientChannel objects in Opened, Faulted, and Closing
    // states. DumpDetective wcf-channels scans objects whose type name starts with
    // "System.ServiceModel." and reads their state/remoteAddress/faultReason fields.
    private static readonly List<System.ServiceModel.ClientChannel> _wcfChannels = [];
    private static readonly List<System.ServiceModel.DuplexClientChannel> _duplexChannels = [];

    public static IResult TriggerWcfChannels()
    {
        const string ep = "net.tcp://svc01.contoso.com:8080/OrderService";
        const string binding = "NetTcpBinding";

        // 25 healthy Opened channels
        for (int i = 0; i < 25; i++)
            _wcfChannels.Add(new System.ServiceModel.ClientChannel(System.ServiceModel.CommunicationState.Opened, ep, binding));

        // 15 faulted channels (socket timeout)
        for (int i = 0; i < 15; i++)
            _wcfChannels.Add(new System.ServiceModel.ClientChannel(
                System.ServiceModel.CommunicationState.Faulted, ep, binding,
                $"The socket connection was aborted after {30 + i}s inactivity."));

        // 10 duplex channels in Opened state
        for (int i = 0; i < 10; i++)
            _duplexChannels.Add(new System.ServiceModel.DuplexClientChannel(
                System.ServiceModel.CommunicationState.Opened, ep, "WSDualHttpBinding"));

        int total = _wcfChannels.Count + _duplexChannels.Count;
        return Results.Ok(new
        {
            message = $"{total} WCF channel objects on heap.",
            breakdown = new { clientChannels = _wcfChannels.Count, duplexChannels = _duplexChannels.Count, faulted = 15 },
            command = "DumpDetective wcf-channels <dump.dmp>",
        });
    }

    public static string WcfStatus => $"wcf-channels: {_wcfChannels.Count + _duplexChannels.Count} channel objects";

    public static void Reset()
    {
        _connections.Clear();
        _wcfChannels.Clear();
        _duplexChannels.Clear();
    }
}
