using DumpDetective.ScenarioHost;
using Microsoft.Diagnostics.NETCore.Client;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ── Entry point ───────────────────────────────────────────────────────────────
//
// Usage:  DumpDetective.ScenarioHost <scenario-command-name>
//
// Runs the requested scenario's Setup(), captures a heap dump of this process,
// writes it to %TEMP%\DumpDetective\Scenarios\<name>.dmp, prints the full path
// to stdout, then runs Teardown() and exits.
//
// Called by CommandContext<TScenario> in the test project when no cached dump
// exists for a given scenario.
//
// Exit codes:
//   0  dump written successfully
//   1  unknown scenario / write failure
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DumpDetective.ScenarioHost <scenario-name>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Available scenarios:");
    foreach (var name in ScenarioRegistry.All.Keys.OrderBy(k => k))
        Console.Error.WriteLine($"  {name}");
    return 1;
}

string scenarioName = args[0];
if (!ScenarioRegistry.All.TryGetValue(scenarioName, out var scenario))
{
    Console.Error.WriteLine($"Unknown scenario: '{scenarioName}'");
    Console.Error.WriteLine("Run without arguments to list available scenarios.");
    return 1;
}

// Determine output path
string dumpDir  = Path.Combine(
    Environment.GetEnvironmentVariable("DD_TEST_DUMP_DIR") ?? DefaultDumpDir(),
    "Scenarios");
Directory.CreateDirectory(dumpDir);
string dumpPath = Path.Combine(dumpDir, $"{scenarioName}.dmp");

// Delete stale dump so we always produce a fresh one
if (File.Exists(dumpPath)) File.Delete(dumpPath);

try
{
    // 1. Set up the scenario state in THIS process
    scenario.Setup();

    // 2. Small settle time so async tasks / threads reach their blocked state
    await Task.Delay(300);

    // 3. Capture a heap dump of this process
    var client = new DiagnosticsClient(Environment.ProcessId);
    client.WriteDump(DumpType.WithHeap, dumpPath, logDumpGeneration: false);

    if (!File.Exists(dumpPath))
    {
        Console.Error.WriteLine($"Dump was not created at '{dumpPath}'.");
        return 1;
    }

    // 4. Print the path for the caller (CommandContext reads this)
    Console.WriteLine(dumpPath);

    // 5. Teardown (unblocks gates, frees handles, etc.)
    try { scenario.Teardown(); }
    catch { /* best-effort */ }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ScenarioHost failed for '{scenarioName}': {ex.Message}");
    return 1;
}

static string DefaultDumpDir() =>
    Path.Combine(Path.GetTempPath(), "DumpDetective");

// ── Registry ──────────────────────────────────────────────────────────────────

namespace DumpDetective.ScenarioHost
{
    internal sealed class ScenarioEntry
    {
        public Action  Setup    { get; init; } = static () => { };
        public Action  Teardown { get; init; } = static () => { };
    }

    internal static class ScenarioRegistry
    {
        public static readonly Dictionary<string, ScenarioEntry> All = Build();

        private static Dictionary<string, ScenarioEntry> Build()
        {
            var d = new Dictionary<string, ScenarioEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Scenarios.All)
                d[s.CommandName] = new ScenarioEntry { Setup = s.Setup, Teardown = s.Teardown };
            return d;
        }
    }
}
