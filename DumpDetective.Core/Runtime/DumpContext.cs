using Microsoft.Diagnostics.Runtime;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.IO;

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
    private readonly HashSet<Type> _pinnedAnalysisTypes = [];
    // Thread-safe once-computed results (e.g. thread name map shared by multiple parallel commands)
    private readonly ConcurrentDictionary<Type, object> _onceCache = new();

    public string     DumpPath    { get; }
    public DateTime   FileTime    { get; }
    public ClrRuntime Runtime     { get; }
    public ClrHeap    Heap        => Runtime.Heap;
    public string?    ClrVersion  => GetClrVersion();

    /// <summary>
    /// Returns a human-readable CLR version string.
    /// For .NET Core dumps, <c>ClrInfo.Version</c> returns "0.0" because ClrMD cannot parse
    /// the version from the module info. We fall back to:
    /// 1. <c>ClrInfo.ModuleInfo.Version</c> — the file version of the CLR module (e.g. 8.0.x.y)
    /// 2. The DAC filename in <c>ClrInfo.DebuggingLibraries</c> — e.g. <c>mscordaccore_amd64_amd64_8.0.3.23906.dll</c>
    /// </summary>
    private string? GetClrVersion()
    {
        var clrInfo = Runtime.ClrInfo;
        if (clrInfo is null) return null;

        var v = clrInfo.Version;
        if (v is not null && (v.Major != 0 || v.Minor != 0))
        {
            // Prefix .NET vs CLR based on flavor for clarity
            string prefix = IsCoreRuntime ? ".NET " : "CLR ";
            return $"{prefix}{v.Major}.{v.Minor}";
        }

        // Fallback 1: ModuleInfo.Version (file version of the CLR module)
        try
        {
            var modVer = clrInfo.ModuleInfo.Version;
            if (modVer is not null && modVer.Major != 0)
            {
                string prefix = IsCoreRuntime ? ".NET " : "CLR ";
                return $"{prefix}{modVer.Major}.{modVer.Minor}";
            }
        }
        catch { /* non-critical */ }

        // Fallback 2: extract from DAC filename in DebuggingLibraries
        // Format: mscordaccore_<arch>_<arch>_<major>.<minor>.<build>.<rev>.dll
        try
        {
            foreach (var lib in clrInfo.DebuggingLibraries)
            {
                if (lib.FileName is null) continue;
                var name = Path.GetFileNameWithoutExtension(lib.FileName);
                var lastUnderscore = name.LastIndexOf('_');
                if (lastUnderscore < 0) continue;
                var versionPart = name[(lastUnderscore + 1)..];
                if (versionPart.Length == 0 || !char.IsDigit(versionPart[0])) continue;
                var parts = versionPart.Split('.');
                if (parts.Length < 2) continue;
                string prefix = IsCoreRuntime ? ".NET " : "CLR ";
                return $"{prefix}{parts[0]}.{parts[1]}";
            }
        }
        catch { /* non-critical */ }

        // Fallback 3: scan module paths via EnumerateModules() for a versioned CLR path.
        // Linux: /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.21/libcoreclr.so
        // Windows: C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.21\coreclr.dll
        // Avoids accessing ClrInfo.ModuleInfo which can corrupt the data reader state on
        // cross-platform (Linux-dump-on-Windows) scenarios.
        try
        {
            foreach (var module in Runtime.EnumerateModules())
            {
                var moduleName = module.Name;
                if (string.IsNullOrEmpty(moduleName)) continue;
                var ver = ExtractVersionFromModulePath(moduleName);
                if (ver is not null)
                {
                    string prefix = IsCoreRuntime ? ".NET " : "CLR ";
                    return $"{prefix}{ver}";
                }
            }
        }
        catch { /* non-critical */ }

        return v?.ToString();
    }

    /// <summary>
    /// Extracts a Major.Minor version string from a .NET runtime module path.
    /// Looks for path segments (e.g. "8.0.21") where the first part is a non-zero integer.
    /// Returns "8.0" for "/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.21/libcoreclr.so".
    /// </summary>
    private static string? ExtractVersionFromModulePath(string modulePath)
    {
        foreach (var segment in modulePath.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || !char.IsDigit(segment[0])) continue;
            var dotParts = segment.Split('.');
            if (dotParts.Length >= 2 &&
                int.TryParse(dotParts[0], out int maj) &&
                int.TryParse(dotParts[1], out int min) &&
                maj > 0)
            {
                return $"{maj}.{min}";
            }
        }
        return null;
    }


    /// Used to disable parallel segment walking in <c>HeapWalker</c>, which is unsafe on
    /// .NET Core dumps where the underlying data reader is not thread-safe across segments.
    /// </summary>
    public bool IsCoreRuntime { get; }

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

    /// <summary>
    /// Returns the cached result for <typeparamref name="T"/>, invoking <paramref name="factory"/>
    /// exactly once across all parallel callers. If two threads call this simultaneously for the
    /// same <typeparamref name="T"/>, only one runs the factory — the other blocks until it finishes.
    /// </summary>
    public T GetOrCreateAnalysis<T>(Func<T> factory) where T : class
    {
        var lazy = (Lazy<T>)_onceCache.GetOrAdd(typeof(T), _ => (object)new Lazy<T>(factory));
        return lazy.Value;
    }

    /// <summary>
    /// Seeds the once-cache with an already-computed value so that subsequent
    /// <see cref="GetOrCreateAnalysis{T}"/> calls return it immediately without executing a factory.
    /// Call this during collection to pre-populate expensive caches before parallel sub-reports run.
    /// </summary>
    public void PreloadAnalysis<T>(T value) where T : class
        => _onceCache.TryAdd(typeof(T), (object)new Lazy<T>(() => value));

    /// <summary>
    /// Replaces the cached value for <typeparamref name="T"/> with <paramref name="replacement"/>.
    /// Use this to release a large in-memory cache after all consumers have finished
    /// (e.g. replace a loaded <c>BfsCacheBox</c> with an empty one to free the CSR arrays).
    /// </summary>
    public void ReplaceAnalysis<T>(T replacement) where T : class
    {
        _onceCache[typeof(T)] = (object)new Lazy<T>(() => replacement);
    }

    /// <summary>
    /// Pins <paramref name="analysisType"/> so batch cleanup code can avoid releasing it
    /// while dependent commands are still running.
    /// </summary>
    public void PinAnalysis(Type analysisType)
    {
        lock (_pinnedAnalysisTypes)
            _pinnedAnalysisTypes.Add(analysisType);
    }

    /// <summary>
    /// Removes a previous pin for <paramref name="analysisType"/>.
    /// </summary>
    public void UnpinAnalysis(Type analysisType)
    {
        lock (_pinnedAnalysisTypes)
            _pinnedAnalysisTypes.Remove(analysisType);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="analysisType"/> is currently pinned.
    /// </summary>
    public bool IsAnalysisPinned(Type analysisType)
    {
        lock (_pinnedAnalysisTypes)
            return _pinnedAnalysisTypes.Contains(analysisType);
    }

    /// <summary>
    /// Replaces the cached value for <typeparamref name="T"/> unless that type is pinned.
    /// Returns <see langword="true"/> when the replacement was applied.
    /// </summary>
    public bool TryReplaceAnalysis<T>(T replacement) where T : class
    {
        if (IsAnalysisPinned(typeof(T))) return false;
        ReplaceAnalysis(replacement);
        return true;
    }

    private DumpContext(string path, DataTarget dt, ClrRuntime rt, string? archWarning, bool isCoreRuntime)
    {
        DumpPath      = path;
        FileTime      = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.UtcNow;
        _dt           = dt;
        Runtime       = rt;
        ArchWarning   = archWarning;
        IsCoreRuntime = isCoreRuntime;
    }

    public static DumpContext Open(string path)
    {
        var dt = DataTarget.LoadDump(path);
        var clrInfo = dt.ClrVersions.FirstOrDefault();
        var rt = clrInfo?.CreateRuntime();
        if (rt is null)
        {
            dt.Dispose();
            throw new InvalidOperationException("No CLR runtime found in dump.");
        }

        // Parallel segment walking is unsafe on .NET Core / NativeAOT dumps:
        // the underlying data reader is not thread-safe across concurrent segment reads,
        // which causes "The handle is invalid" errors under Parallel.ForEach.
        bool isCoreRuntime = clrInfo?.Flavor is ClrFlavor.Core or ClrFlavor.NativeAOT;

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

        return new DumpContext(path, dt, rt, archWarning, isCoreRuntime);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshot?.Dispose();
        Runtime.Dispose();
        _dt.Dispose();
    }
}
