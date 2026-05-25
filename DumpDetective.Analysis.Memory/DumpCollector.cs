using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Interfaces;
using System.Diagnostics;
using System.IO;

namespace DumpDetective.Analysis.Memory;

public static class DumpCollector
{

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Full collection (string dups + event leaks) using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectFull(DumpContext ctx, Action<string>? progress = null)
        => CollectFromContext(ctx, full: true, progress);

    /// <summary>
    /// Full collection with command-provided heap contributors that piggyback on the
    /// shared heap walk and publish their results into <see cref="DumpContext"/>.
    /// </summary>
    public static DumpSnapshot CollectFull(DumpContext ctx,
                                           IReadOnlyList<ICommandHeapContributor> heapContributors,
                                           Action<string>? progress = null)
        => CollectFromContext(ctx, full: true, progress, heapContributors: heapContributors);

    /// <summary>
    /// Full collection with additional consumers that piggyback on the single heap walk.
    /// Use this to collect e.g. <see cref="FragmentationConsumer"/> or <see cref="BfsPass1Consumer"/>
    /// without a second heap enumeration.
    /// </summary>
    public static DumpSnapshot CollectFull(DumpContext ctx, IReadOnlyList<IHeapObjectConsumer> extraConsumers, Action<string>? progress = null)
        => CollectFromContext(ctx, full: true, progress, extraConsumers);

    /// <summary>
    /// Cache-build-only walk: runs only the four consumers needed to populate
    /// <c>ctx.Snapshot</c> (TypeStats, InboundRef, StringGroups, GenCounter) plus
    /// any <paramref name="extraConsumers"/>. Heavy analysis consumers are omitted.
    /// After the main walk, runs <c>EnumerateFinalizableObjects()</c> sequentially
    /// and dispatches to <paramref name="finalizableConsumers"/> if provided.
    /// Use this from <c>LoadCommand</c> where the goal is writing cache files only.
    /// </summary>
    public static void CollectForLoad(DumpContext ctx,
                                      IReadOnlyList<IHeapObjectConsumer> extraConsumers,
                                      Action<string>? progress = null,
                                      IReadOnlyList<IFinalizableObjectConsumer>? finalizableConsumers = null)
        => HeapObjectCollector.CollectForCacheBuild(ctx, extraConsumers, progress, finalizableConsumers);

    /// <summary>Lightweight collection using an existing <see cref="DumpContext"/>.</summary>
    public static DumpSnapshot CollectLightweight(DumpContext ctx, Action<string>? progress = null)
        => CollectFromContext(ctx, full: false, progress);

    /// <summary>Full collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectFull(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: true, progress);

    /// <summary>Lightweight collection — opens its own DataTarget from <paramref name="dumpPath"/>.</summary>
    public static DumpSnapshot CollectLightweight(string dumpPath, Action<string>? progress = null)
        => Collect(dumpPath, full: false, progress);

    // ── Private collect paths ─────────────────────────────────────────────────

    private static DumpSnapshot CollectFromContext(DumpContext ctx, bool full, Action<string>? progress = null,
                                                   IReadOnlyList<IHeapObjectConsumer>? extraConsumers = null,
                                                   IReadOnlyList<ICommandHeapContributor>? heapContributors = null)
    {
        var snapshot = CreateSnapshot(ctx.DumpPath, ctx.FileTime, full);
        snapshot.ClrVersion = ctx.ClrVersion;
        CollectAll(ctx.Runtime, snapshot, full, progress, ctx, extraConsumers, heapContributors);
        var (findings, score) = HealthScorer.Score(snapshot, ThresholdLoader.Current.Scoring);
        snapshot.Findings    = findings.ToList();
        snapshot.HealthScore = score;
        return snapshot;
    }

    private static DumpSnapshot Collect(string dumpPath, bool full, Action<string>? progress = null)
    {
        var snapshot = CreateSnapshot(
            dumpPath,
            File.Exists(dumpPath) ? File.GetLastWriteTime(dumpPath) : DateTime.UtcNow,
            full);

        var (runtime, dataTarget) = DumpHelpers.OpenDump(dumpPath);
        using var _dt = dataTarget;
        using var _rt = runtime;

        if (runtime is null) return snapshot;

        snapshot.ClrVersion = GetClrVersionString(runtime);
        return FinalizeSnapshot(runtime, snapshot, full, progress);
    }

    /// <summary>
    /// Returns a human-readable CLR version string, using the same fallback logic as
    /// <see cref="DumpContext.GetClrVersion"/>: when the parsed version is 0.0 (common
    /// for .NET Core dumps), we try to extract the version from <c>ClrInfo.ModuleInfo.Version</c>
    /// or the DAC filename in <c>ClrInfo.DebuggingLibraries</c>.
    /// </summary>
    private static string? GetClrVersionString(ClrRuntime runtime)
    {
        var clrInfo = runtime.ClrInfo;
        if (clrInfo is null) return null;

        bool isCoreRuntime = clrInfo.Flavor is ClrFlavor.Core or ClrFlavor.NativeAOT;

        var v = clrInfo.Version;
        if (v is not null && (v.Major != 0 || v.Minor != 0))
        {
            string prefix = isCoreRuntime ? ".NET " : "CLR ";
            return $"{prefix}{v.Major}.{v.Minor}";
        }

        // Fallback 1: ModuleInfo.Version (file version of the CLR module)
        try
        {
            var modVer = clrInfo.ModuleInfo.Version;
            if (modVer is not null && modVer.Major != 0)
            {
                string prefix = isCoreRuntime ? ".NET " : "CLR ";
                return $"{prefix}{modVer.Major}.{modVer.Minor}";
            }
        }
        catch { /* non-critical */ }

        // Fallback 2: extract from DAC filename in DebuggingLibraries
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
                string prefix = isCoreRuntime ? ".NET " : "CLR ";
                return $"{prefix}{parts[0]}.{parts[1]}";
            }
        }
        catch { /* non-critical */ }

        // Fallback 3: scan module paths via EnumerateModules() for a versioned CLR path.
        // Linux: /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.21/libcoreclr.so
        // Windows: C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.21\coreclr.dll
        // Avoids accessing ClrInfo.ModuleInfo.FileName which can corrupt the data reader on
        // cross-platform (Linux-dump-on-Windows) scenarios.
        try
        {
            foreach (var module in runtime.EnumerateModules())
            {
                var moduleName = module.Name;
                if (string.IsNullOrEmpty(moduleName)) continue;
                var ver = ExtractVersionFromModulePath(moduleName);
                if (ver is not null)
                {
                    string prefix = isCoreRuntime ? ".NET " : "CLR ";
                    return $"{prefix}{ver}";
                }
            }
        }
        catch { /* non-critical */ }

        return v?.ToString();
    }

    /// <summary>
    /// Extracts a Major.Minor version string from a .NET runtime module path.
    /// Looks for numeric path segments (e.g. "8.0.21") with a non-zero major version.
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

    private static DumpSnapshot CreateSnapshot(string dumpPath, DateTime fileTime, bool full)
        => new()
        {
            DumpPath          = dumpPath,
            DumpFileSizeBytes = File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0,
            FileTime          = fileTime,
            IsFullMode        = full,
        };

    private static DumpSnapshot FinalizeSnapshot(ClrRuntime runtime, DumpSnapshot snapshot, bool full, Action<string>? progress)
    {
        CollectAll(runtime, snapshot, full, progress);
        var (findings, score) = HealthScorer.Score(snapshot, ThresholdLoader.Current.Scoring);
        snapshot.Findings    = findings.ToList();
        snapshot.HealthScore = score;
        return snapshot;
    }

    /// <summary>
    /// Core collection logic. When <paramref name="ctx"/> is provided and
    /// <paramref name="full"/> is <see langword="true"/>, a single combined heap walk
    /// builds both the <see cref="DumpSnapshot"/> fields and the cached
    /// <see cref="HeapSnapshot"/> in one pass, eliminating the second enumeration
    /// that <c>EnsureSnapshot</c> would otherwise trigger.
    /// </summary>
    private static void CollectAll(ClrRuntime runtime, DumpSnapshot snapshot, bool full,
                                   Action<string>? progress = null, DumpContext? ctx = null,
                                   IReadOnlyList<IHeapObjectConsumer>? extraConsumers = null,
                                   IReadOnlyList<ICommandHeapContributor>? heapContributors = null)
    {
        // Track elapsed time per sub-collector only when a progress listener is attached
        var sw = progress is not null ? Stopwatch.StartNew() : null;

        RuntimeSubCollectors.CollectThreads(runtime, snapshot);
        RuntimeSubCollectors.CollectThreadPool(runtime, snapshot);
        if (sw is not null)
        {
            progress!($"[SCAN]Thread scan|{snapshot.ThreadCount}|{sw.ElapsedMilliseconds}");
            sw.Restart();
        }

        RuntimeSubCollectors.CollectHandles(runtime, snapshot, progress);
        if (sw is not null)
        {
            progress!($"[SCAN]Handle scan|{snapshot.TotalHandleCount}|{sw.ElapsedMilliseconds}");
            sw.Restart();
        }

        RuntimeSubCollectors.CollectModules(runtime, snapshot);
        if (sw is not null)
        {
            progress!($"[SCAN]Module scan|{snapshot.ModuleCount}|{sw.ElapsedMilliseconds}");
        }

        var heap = runtime.Heap;
        if (heap.CanWalkHeap)
        {
            RuntimeSubCollectors.CollectSegmentLayout(heap, snapshot);
            if (ctx is not null && full)
                HeapObjectCollector.CollectHeapObjectsCombined(ctx, snapshot, progress, extraConsumers, heapContributors);
            else
            {
                // Detect .NET Core / NativeAOT from runtime when ctx is unavailable:
                // parallel segment walking is unsafe on those runtimes.
                bool seqOnly = ctx?.IsCoreRuntime
                    ?? (runtime.ClrInfo?.Flavor is ClrFlavor.Core or ClrFlavor.NativeAOT);
                HeapObjectCollector.CollectHeapObjects(heap, snapshot, full, progress, seqOnly);
            }

            if (sw is not null) sw.Restart();
            RuntimeSubCollectors.CollectFinalizerQueue(heap, snapshot, progress);
            if (sw is not null)
                progress!($"[SCAN]Finalizer queue scan|{snapshot.FinalizerQueueDepth}|{sw.ElapsedMilliseconds}");
        }
    }
}

