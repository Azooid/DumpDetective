using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace DumpDetective.Core.Runtime;

/// <summary>
/// Opens a dump file once and exposes <see cref="ClrRuntime"/> and
/// <see cref="ClrHeap"/> for reuse across multiple data collection calls
/// within a single command execution.
/// </summary>
public sealed class DumpContext : IDisposable
{
    private readonly DataTarget _dt;
    private bool _disposed;
    private HeapSnapshot? _snapshot;
    private readonly Dictionary<Type, object> _analysisCache = new();

    public string     DumpPath    { get; }
    public DateTime   FileTime    { get; }
    public ClrRuntime Runtime     { get; }
    public ClrHeap    Heap        => Runtime.Heap;
    public string?    ClrVersion  => Runtime.ClrInfo?.Version.ToString();

    /// <summary>
    /// Non-null when the dump architecture differs from the tool's process architecture.
    /// </summary>
    public string? ArchWarning { get; }

    /// <summary>Cached heap snapshot, non-null after <see cref="EnsureSnapshot"/> is called.</summary>
    internal HeapSnapshot? Snapshot => _snapshot;

    /// <summary>
    /// Builds (or returns the cached) heap snapshot — a single
    /// <c>EnumerateObjects</c> walk that collects type stats, inbound reference
    /// counts, string groups, and generation counts.
    /// </summary>
    internal HeapSnapshot EnsureSnapshot()
    {
        _snapshot ??= HeapSnapshot.Build(this);
        return _snapshot;
    }

    /// <summary>
    /// Injects a <see cref="HeapSnapshot"/> built externally (e.g. by
    /// <c>DumpCollector.CollectFull</c>) so subsequent <see cref="EnsureSnapshot"/>
    /// calls return it without re-walking the heap.
    /// </summary>
    internal void PreloadSnapshot(HeapSnapshot snap) => _snapshot ??= snap;

    /// <summary>
    /// Returns a previously cached per-command analysis result, or <see langword="null"/>
    /// if it has not been pre-populated yet.
    /// </summary>
    public T? GetAnalysis<T>() where T : class
        => _analysisCache.TryGetValue(typeof(T), out var v) ? (T)v : null;

    /// <summary>
    /// Stores a per-command analysis result in the cache. Subsequent
    /// <see cref="GetAnalysis{T}"/> calls for the same type return this value.
    /// </summary>
    internal void SetAnalysis<T>(T value) where T : class
        => _analysisCache[typeof(T)] = value;

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
        var dt = DataTarget.LoadDump(path);
        var rt = dt.ClrVersions.FirstOrDefault()?.CreateRuntime();
        if (rt is null)
        {
            dt.Dispose();
            throw new InvalidOperationException("No CLR runtime found in dump.");
        }

        string? archWarning = null;
        try
        {
            var dumpArch  = dt.DataReader.Architecture;
            bool dumpIs32 = dumpArch is Architecture.X86 or Architecture.Arm;
            bool toolIs32 = IntPtr.Size == 4;
            if (dumpIs32 != toolIs32)
                archWarning = $"Dump architecture is {dumpArch} ({(dumpIs32 ? "32-bit" : "64-bit")}) " +
                              $"but this tool is running as {(toolIs32 ? "32-bit" : "64-bit")} — " +
                              $"some pointer-width-sensitive field reads may be unreliable.";
        }
        catch { /* non-critical */ }

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
