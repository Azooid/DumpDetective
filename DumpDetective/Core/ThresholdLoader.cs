using System.Text.Json;
using DumpDetective.Models;

namespace DumpDetective.Core;

/// <summary>
/// Loads <see cref="ThresholdConfig"/> from <c>dd-thresholds.json</c> once
/// and caches it for the lifetime of the process.
///
/// Search order:
///   1. Next to the executable  (AppContext.BaseDirectory)
///   2. Current working directory
///
/// If no file is found, or the file contains invalid JSON, built-in defaults
/// are used and no error is raised.
/// </summary>
public static class ThresholdLoader
{
    private const string FileName = "dd-thresholds.json";

    private static ThresholdConfig? _cached;

    /// <summary>Returns the active threshold configuration (loaded lazily on first access).</summary>
    public static ThresholdConfig Current => _cached ??= Load();

    private static ThresholdConfig Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Directory.GetCurrentDirectory(), FileName),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var cfg  = JsonSerializer.Deserialize(json, ThresholdConfigContext.Default.ThresholdConfig);
                if (cfg is not null)
                    return cfg;
            }
            catch
            {
                // Bad/empty JSON — fall through to defaults
            }
        }

        return new ThresholdConfig();
    }
}
