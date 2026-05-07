namespace DumpDetective.Core.Tracing;

/// <summary>
/// Static factory that provides the default set of framework collapse rules.
/// Rules are evaluated in order; the first match wins per-frame.
/// </summary>
public static class CollapseRuleSet
{
    /// <summary>
    /// Returns the default rule set covering async infrastructure, ASP.NET pipeline,
    /// Entity Framework, generic ORM LINQ, and common serialization layers.
    /// </summary>
    public static IReadOnlyList<CollapseRule> Default() =>
    [
        // ── Async / TPL infrastructure ────────────────────────────────────────
        new CollapseRule(
            Patterns:       ["AsyncTaskMethodBuilder", "MoveNext", "Task.InnerInvoke",
                             "ExecutionContext.RunInternal", "ThreadPoolWorkQueue"],
            CollapsedLabel: "[Async/TPL Infrastructure]",
            Category:       "Async"),

        // ── ASP.NET (Classic and Core) pipeline ───────────────────────────────
        new CollapseRule(
            Patterns:       ["DelegatingHandler.SendAsync", "HttpServer.ProcessRequestAsync",
                             "ControllerActionInvoker.InvokeActionAsync",
                             "ResourceInvoker.InvokeAsync", "HttpApplication.ExecuteStep",
                             "PipelineStepManager", "IHttpAsyncHandler.BeginProcessRequest",
                             "RequestContext.RouteData"],
            CollapsedLabel: "[ASP.NET Request Pipeline]",
            Category:       "ASP.NET"),

        // ── Entity Framework shaper / materialisation ─────────────────────────
        new CollapseRule(
            Patterns:       ["Shaper.HandleEntityAppendOnly", "Shaper.HandleIEntityWithKey",
                             "ObjectContext.ExecuteStoreQuery",
                             "EntityCollection.Load", "EntityCollection.GetResults",
                             "ObjectQuery.Execute", "ObjectMaterializer",
                             "DbDataReader.GetValues", "EntityFramework.Core.Query"],
            CollapsedLabel: "[EF Materialisation]",
            Category:       "Entity Framework"),

        // ── Newtonsoft / System.Text.Json serialisation ───────────────────────
        new CollapseRule(
            Patterns:       ["Newtonsoft.Json.JsonSerializer", "Newtonsoft.Json.JsonWriter",
                             "Newtonsoft.Json.JsonReader", "Newtonsoft.Json.Serialization",
                             "System.Text.Json.Utf8JsonWriter", "System.Text.Json.JsonSerializer"],
            CollapsedLabel: "[JSON Serialisation]",
            Category:       "Serialisation"),

        // ── WCF channel / proxy ───────────────────────────────────────────────
        new CollapseRule(
            Patterns:       ["System.ServiceModel.Channels", "ServiceModel.Dispatcher",
                             "ClientOperation.BeginInvoke", "ChannelFactory"],
            CollapsedLabel: "[WCF Channel]",
            Category:       "WCF"),

        // ── gRPC ─────────────────────────────────────────────────────────────
        new CollapseRule(
            Patterns:       ["Grpc.Core.Internal", "Google.Protobuf", "Grpc.Net.Client"],
            CollapsedLabel: "[gRPC]",
            Category:       "gRPC"),

        // ── SignalR hub dispatch ───────────────────────────────────────────────
        new CollapseRule(
            Patterns:       ["Microsoft.AspNetCore.SignalR", "HubConnectionHandler",
                             "SignalR.DefaultHubActivator"],
            CollapsedLabel: "[SignalR Hub]",
            Category:       "SignalR"),

        // ── AutoMapper ───────────────────────────────────────────────────────
        new CollapseRule(
            Patterns:       ["AutoMapper.Mapper", "AutoMapper.MapperConfiguration",
                             "AutoMapper.TypeMap"],
            CollapsedLabel: "[AutoMapper]",
            Category:       "Mapping"),
    ];
}
