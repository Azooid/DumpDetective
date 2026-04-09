using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core;

/// <summary>
/// Opens a dump file once and exposes the ClrRuntime and ClrHeap for reuse
/// across multiple data collection calls within a single command execution.
/// </summary>
public sealed class DumpContext : IDisposable
{
    readonly DataTarget _dt;
    bool _disposed;

    public string     DumpPath   { get; }
    public DateTime   FileTime   { get; }
    public ClrRuntime Runtime    { get; }
    public ClrHeap    Heap       => Runtime.Heap;
    public string?    ClrVersion => Runtime.ClrInfo?.Version.ToString();

    private DumpContext(string path, DataTarget dt, ClrRuntime rt)
    {
        DumpPath = path;
        FileTime = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.UtcNow;
        _dt      = dt;
        Runtime  = rt;
    }

    public static DumpContext Open(string path)
    {
        var dt = DataTarget.LoadDump(path);
        var rt = dt.ClrVersions.FirstOrDefault()?.CreateRuntime();
        if (rt is null)
        {
            dt.Dispose();
            throw new InvalidOperationException("No CLR runtime found in dump.");
        }
        return new DumpContext(path, dt, rt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runtime.Dispose();
        _dt.Dispose();
    }
}
