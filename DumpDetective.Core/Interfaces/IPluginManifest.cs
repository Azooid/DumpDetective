namespace DumpDetective.Core.Interfaces;

/// <summary>
/// Entry point that every DumpDetective plugin assembly must expose.
/// The plugin loader reflects over the assembly, finds the single concrete
/// class that implements this interface, instantiates it with the default
/// constructor, and calls <see cref="RegisterCommands"/> to obtain the
/// commands the plugin contributes.
/// </summary>
public interface IPluginManifest
{
    /// <summary>Human-readable name of the plugin, e.g. "MyCompany.DumpDetective.Plugins".</summary>
    string PluginName { get; }

    /// <summary>Optional version string shown in --help output.</summary>
    string? Version { get; }

    /// <summary>
    /// Returns the commands contributed by this plugin.
    /// Called exactly once at startup.  The returned collection must not be null.
    /// </summary>
    IEnumerable<ICommand> RegisterCommands();
}
