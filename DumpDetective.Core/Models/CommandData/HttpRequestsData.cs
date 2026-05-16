namespace DumpDetective.Core.Models.CommandData;

public sealed record HttpRequestsData(
    IReadOnlyList<HttpObjectEntry>     Objects,
    /// <summary>
    /// Async state machines whose method names suggest they are serving in-flight HTTP requests.
    /// Populated by cross-referencing <c>AsyncStacksData</c> with the in-flight request URIs/methods.
    /// Empty list when no <c>AsyncStacksData</c> is available.
    /// </summary>
    IReadOnlyList<AsyncHttpCorrelation> AsyncCorrelations = default!,
    /// <summary>
    /// Active server-side connection pools (System.Net.ServicePoint).
    /// Each entry represents a unique remote endpoint this process is connected to.
    /// Visible even when request objects have already been GC'd.
    /// </summary>
    IReadOnlyList<ServicePointEntry>   ServicePoints = default!);

public sealed record HttpObjectEntry(
    string  Type,
    ulong   Addr,
    long    Size,
    string  Method,
    string  Uri,
    int     StatusCode);

/// <summary>
/// A live <c>System.Net.ServicePoint</c> entry representing one outbound HTTP connection pool.
/// </summary>
public sealed record ServicePointEntry(
    ulong  Addr,
    string Address,
    string Host,
    int    Port,
    int    CurrentConnections,
    int    ConnectionLimit);

/// <summary>
/// An async state machine that appears to be serving an in-flight HTTP request.
/// The correlation is heuristic: the state machine's method name contains terms
/// like "Controller", "Handler", "Request", "Http", or "Endpoint".
/// </summary>
public sealed record AsyncHttpCorrelation(
    string StateMachineMethod,
    string State,
    ulong  Addr,
    string CorrelationHint);
