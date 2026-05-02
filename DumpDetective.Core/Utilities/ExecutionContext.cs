namespace DumpDetective.Core.Utilities;

/// <summary>
/// Manages thread-local execution state shared between orchestrators and
/// sub-command workers: verbose-output suppression and parameter override
/// dictionaries.
/// </summary>
public static class ExecutionContext
{
    // ── Verbose suppression ───────────────────────────────────────────────────

    [ThreadStatic] private static bool _suppressVerbose;

    /// <summary>
    /// When <see langword="true"/> on the current thread, console spinners and
    /// progress output are suppressed. <c>[ThreadStatic]</c> so each parallel
    /// full-analyze worker is isolated.
    /// </summary>
    public static bool SuppressVerbose
    {
        get => _suppressVerbose;
        set => _suppressVerbose = value;
    }

    // ── Parameter overrides ───────────────────────────────────────────────────

    /// <summary>
    /// Per-thread overrides set by orchestrators before running sub-commands.
    /// Keyed by the same names used in <see cref="CliArgs"/> (lowercase, no dashes).
    /// </summary>
    [ThreadStatic] private static Dictionary<string, string>? _overrides;

    /// <summary>
    /// Shared (cross-thread) overrides published by the orchestrator before spawning
    /// parallel workers. Only written from the main thread before Parallel.ForEach;
    /// never written during parallel execution, so no lock is needed.
    /// </summary>
    private static volatile Dictionary<string, string>? _sharedOverrides;

    /// <summary>
    /// Sets a per-thread override. Use <see cref="SetSharedOverride"/> when the
    /// value must be visible to parallel worker threads.
    /// </summary>
    public static void SetOverride(string key, string value)
    {
        _overrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _overrides[key] = value;
    }

    /// <summary>
    /// Sets a shared (cross-thread) override. Call from the orchestrator thread
    /// before launching parallel sub-report workers.
    /// </summary>
    public static void SetSharedOverride(string key, string value)
    {
        var d = _sharedOverrides is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(_sharedOverrides, StringComparer.OrdinalIgnoreCase);
        d[key] = value;
        _sharedOverrides = d; // atomic reference swap — volatile write
    }

    /// <summary>Clears all per-thread AND shared overrides.</summary>
    public static void ClearOverrides()
    {
        _overrides       = null;
        _sharedOverrides = null;
    }

    /// <summary>
    /// Returns an override value. Checks per-thread overrides first, then shared
    /// overrides, then returns <see langword="null"/>.
    /// </summary>
    public static string? GetOverride(string key)
    {
        if (_overrides is not null && _overrides.TryGetValue(key, out var v)) return v;
        if (_sharedOverrides is not null && _sharedOverrides.TryGetValue(key, out v)) return v;
        return null;
    }

    /// <summary>Returns an override as int, or <paramref name="default"/> if not set.</summary>
    public static int GetOverrideInt(string key, int @default) =>
        GetOverride(key) is string v && int.TryParse(v, out var n) ? n : @default;

    /// <summary>Returns an override as long, or <paramref name="default"/> if not set.</summary>
    public static long GetOverrideLong(string key, long @default) =>
        GetOverride(key) is string v && long.TryParse(v, out var n) ? n : @default;
}
