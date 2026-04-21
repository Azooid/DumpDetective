namespace DumpDetective.Core.Models.CommandData;

public sealed record HttpRequestsData(IReadOnlyList<HttpObjectEntry> Objects);

public sealed record HttpObjectEntry(
    string  Type,
    ulong   Addr,
    long    Size,
    string  Method,
    string  Uri,
    int     StatusCode);
