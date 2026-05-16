using System.Reflection;
using System.Runtime.Loader;
using DumpDetective.Commands.Trace;
using DumpDetective.Core.Interfaces;
using Spectre.Console;

namespace DumpDetective.Cli;

/// <summary>
/// Loads plugin assemblies from well-known directories and extracts their
/// <see cref="IPluginManifest"/> implementations.
///
/// Search order (first match wins for duplicate command names):
///   1. <c>./plugins/</c>     — side-by-side with the executable
///   2. <c>%USERPROFILE%\.dumpdetective\plugins\</c>  (or <c>$HOME/.dumpdetective/plugins/</c>)
///
/// Each plugin lives in its own <see cref="PluginLoadContext"/> so its
/// private dependencies don't conflict with the host or with other plugins.
/// Host assemblies (DumpDetective.Core, ClrMD, Spectre.Console, etc.) are
/// resolved from the default context so plugins share the same type identities.
/// </summary>
internal static class PluginLoader
{
    // Assemblies that must be resolved from the host (shared type identity).
    // Any plugin reference that starts with one of these prefixes uses the
    // default load context instead of the plugin's private folder.
    internal static readonly string[] _hostAssemblyPrefixes =
    [
        "DumpDetective.",
        "Microsoft.Diagnostics.",
        "Spectre.",
        "System.",
        "Microsoft.",
        "netstandard",
        "mscorlib",
    ];

    /// <summary>
    /// Discovers and loads all plugins.  Returns one entry per successfully
    /// loaded plugin; failures are logged to the console and skipped.
    /// </summary>
    internal static IReadOnlyList<LoadedPlugin> LoadAll()
    {
        var searchDirs = GetSearchDirectories();
        var loaded     = new List<LoadedPlugin>();
        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (string pluginDir in Directory.EnumerateDirectories(dir))
            {
                string dirName = Path.GetFileName(pluginDir);
                if (!seen.Add(dirName)) continue; // same name from a different root — skip duplicate

                var plugin = TryLoad(pluginDir);
                if (plugin is not null)
                    loaded.Add(plugin);
            }
        }

        return loaded;
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private static string[] GetSearchDirectories()
    {
        // 1. <exe dir>/plugins/
        string exeDir    = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? ".";
        string exePlugins = Path.Combine(exeDir, "plugins");

        // 2. ~/.dumpdetective/plugins/
        string home       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string homePlugins = Path.Combine(home, ".dumpdetective", "plugins");

        return [exePlugins, homePlugins];
    }

    private static LoadedPlugin? TryLoad(string pluginDir)
    {
        // Convention: the main assembly has the same name as the directory.
        string dirName     = Path.GetFileName(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, dirName + ".dll");

        if (!File.Exists(assemblyPath))
        {
            // Fallback: first .dll in the directory
            assemblyPath = Directory.EnumerateFiles(pluginDir, "*.dll").FirstOrDefault()!;
            if (assemblyPath is null) return null;
        }

        try
        {
            var ctx      = new PluginLoadContext(pluginDir);
            var assembly = ctx.LoadFromAssemblyPath(assemblyPath);

            Type[]? allTypes = null;
            try
            {
                allTypes = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException rtle)
            {
                // Partial load: some types failed (missing deps, etc.); still inspect the rest.
                AnsiConsole.MarkupLine($"[yellow]Plugin warning:[/] [dim]{dirName}[/] — partial type load: {rtle.LoaderExceptions.Count(e => e is not null)} type(s) failed.");
                allTypes = rtle.Types.Where(t => t is not null).ToArray()!;
            }

            Type? manifestType = allTypes
                .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface &&
                                     t.GetInterfaces().Any(i => i.FullName == typeof(IPluginManifest).FullName));

            if (manifestType is null)
            {
                AnsiConsole.MarkupLine($"[yellow]Plugin warning:[/] [dim]{dirName}[/] — no {nameof(IPluginManifest)} implementation found, skipping.");
                return null;
            }

            // Instantiate — must have a public parameterless constructor.
            var instance = (IPluginManifest)Activator.CreateInstance(manifestType)!;
            var commands = instance.RegisterCommands()?.ToList() ?? [];

            // Any plugin command that also implements ITraceSubAnalyzer participates
            // in trace-analyze / trace-dump-analyze when --with-plugins is passed.
            var traceSubAnalyzers = commands
                .OfType<ITraceSubAnalyzer>()
                .ToList();

            // ITracePlugin is the simpler Core-only trace interface (no Commands ref needed).
            var tracePlugins = commands
                .OfType<ITracePlugin>()
                .ToList();

            return new LoadedPlugin(instance.PluginName, instance.Version, commands, traceSubAnalyzers, tracePlugins);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Plugin warning:[/] [dim]{dirName}[/] failed to load — {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}

/// <summary>Holds the commands and trace sub-analyzers contributed by a successfully loaded plugin.</summary>
internal sealed class LoadedPlugin(
    string name,
    string? version,
    IReadOnlyList<ICommand> commands,
    IReadOnlyList<ITraceSubAnalyzer>? traceSubAnalyzers = null,
    IReadOnlyList<ITracePlugin>? tracePlugins = null)
{
    public string                            Name               { get; } = name;
    public string?                           Version            { get; } = version;
    public IReadOnlyList<ICommand>           Commands           { get; } = commands;
    public IReadOnlyList<ITraceSubAnalyzer>  TraceSubAnalyzers  { get; } = traceSubAnalyzers ?? [];
    public IReadOnlyList<ITracePlugin>       TracePlugins       { get; } = tracePlugins ?? [];
}

/// <summary>
/// Per-plugin <see cref="AssemblyLoadContext"/>. Resolves host assemblies from
/// the default context and plugin-private assemblies from the plugin's own directory.
/// </summary>
internal sealed class PluginLoadContext(string pluginDir) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginDir);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Host assemblies → share the default context (same type identities as the host).
        string simpleName = assemblyName.Name ?? string.Empty;
        if (IsHostAssembly(simpleName))
        {
            // Look up by simple name in already-loaded assemblies first.
            // This handles the auto-incrementing AssemblyVersion (3.0.0.*) case where
            // the plugin was compiled against an older build of the same assembly.
            Assembly? already = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            return already; // null → runtime falls through to default ALC (bundle resolution)
        }

        // Plugin-private assembly → load from plugin directory.
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    private static bool IsHostAssembly(string name)
    {
        foreach (string prefix in PluginLoader._hostAssemblyPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
