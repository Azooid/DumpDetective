using Microsoft.Diagnostics.NETCore.Client;

namespace DumpDetective.Tests.Fixtures;

/// <summary>
/// Writes a heap-included mini-dump of the current test-runner process using
/// <see cref="DiagnosticsClient"/> — no external tools required.
/// Takes approximately 1–3 seconds on a small process.
/// </summary>
public static class SelfDumpCapture
{
    /// <summary>
    /// Writes a <see cref="DumpType.WithHeap"/> dump of the current process
    /// into <paramref name="directory"/> and returns the full path.
    /// </summary>
    /// <summary>Default directory used when no override is specified.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Path.GetTempPath(), "DumpDetective");

    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);

        var fileName = $"dd_test_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.dmp";
        var path     = Path.Combine(directory, fileName);

        WriteTo(path);
        return path;
    }

    /// <summary>
    /// Writes a dump to an explicit <paramref name="path"/> (directory is created if missing).
    /// Used by <c>ScenarioHost</c>-mode test infrastructure to write per-scenario dumps.
    /// </summary>
    public static void WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var client = new DiagnosticsClient(Environment.ProcessId);
        client.WriteDump(DumpType.WithHeap, path, logDumpGeneration: false);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"SelfDumpCapture: dump file was not created at '{path}'.");
    }
}
