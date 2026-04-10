using Microsoft.Diagnostics.Runtime;

using System.Runtime.InteropServices;

namespace DumpDetective.Core;

/// <summary>
/// Opens a dump file once and exposes the ClrRuntime and ClrHeap for reuse
/// across multiple data collection calls within a single command execution.
/// </summary>
public sealed class DumpContext : IDisposable
{
    readonly DataTarget _dt;
    bool _disposed;

    public string     DumpPath    { get; }
    public DateTime   FileTime    { get; }
    public ClrRuntime Runtime     { get; }
    public ClrHeap    Heap        => Runtime.Heap;
    public string?    ClrVersion  => Runtime.ClrInfo?.Version.ToString();
    /// <summary>Non-null when the dump architecture differs from this tool's process architecture.</summary>
    public string?    ArchWarning { get; }

    private DumpContext(string path, DataTarget dt, ClrRuntime rt, string? archWarning)
    {
        DumpPath    = path;
        FileTime    = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.UtcNow;
        _dt         = dt;
        Runtime     = rt;
        ArchWarning = archWarning;
    }

    public static DumpContext Open(string path)
    {
        var dt    = DataTarget.LoadDump(path);
        var rt    = dt.ClrVersions.FirstOrDefault()?.CreateRuntime();
        if (rt is null)
        {
            dt.Dispose();
            throw new InvalidOperationException("No CLR runtime found in dump.");
        }

        // Detect architecture mismatch between the dump and this analysis tool
        string? archWarning = null;
        try
        {
            var dumpArch = dt.DataReader.Architecture;
            bool dumpIs32 = dumpArch is Architecture.X86 or Architecture.Arm;
            bool toolIs32 = IntPtr.Size == 4;
            if (dumpIs32 != toolIs32)
                archWarning = $"Dump architecture is {dumpArch} ({(dumpIs32 ? "32-bit" : "64-bit")}) " +
                              $"but this tool is running as {(toolIs32 ? "32-bit" : "64-bit")} — " +
                              $"some pointer-width-sensitive field reads may be unreliable.";
        }
        catch { }

        return new DumpContext(path, dt, rt, archWarning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runtime.Dispose();
        _dt.Dispose();
    }
}
