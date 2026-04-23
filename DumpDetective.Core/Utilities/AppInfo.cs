namespace DumpDetective.Core.Utilities;

/// <summary>
/// Holds the running application's version string.
/// Set once at startup (Program.cs) from the entry assembly; read by sinks and help printers.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// The tool version, e.g. "1.0.4823". Set during application startup before any command runs.
    /// Defaults to "dev" when not set (e.g. during unit tests).
    /// </summary>
    public static string Version { get; set; } = "dev";
}
