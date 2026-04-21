using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Core.Json;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Loads <see cref="ThresholdConfig"/> from <c>dd-thresholds.json</c> once
/// and caches it for the lifetime of the process.
///
/// Search order:
///   1. Next to the executable (<c>AppContext.BaseDirectory</c>)
///   2. Current working directory
///
/// Missing or invalid files silently fall back to built-in defaults.
/// </summary>
public static class ThresholdLoader
{
    private const string  FileName = "dd-thresholds.json";
    private static ThresholdConfig? _cached;

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
                var cfg  = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ThresholdConfig);
                if (cfg is not null) return cfg;
            }
            catch { /* bad JSON — fall through to defaults */ }
        }

        return new ThresholdConfig();
    }
}
