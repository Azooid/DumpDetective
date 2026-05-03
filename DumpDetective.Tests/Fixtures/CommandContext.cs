using DumpDetective.Cli;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.DiagnosticScenarios;
using DumpDetective.Reporting;
using DumpDetective.Reporting.Sinks;
using System.Diagnostics;
using System.Reflection;

namespace DumpDetective.Tests.Fixtures;

/// <summary>
/// xUnit class fixture — created once per test class, disposed after all tests
/// in that class have run.
///
/// Dump resolution order (first match wins):
///   1. Per-scenario cached dump  ─  %TEMP%\DumpDetective\Scenarios\&lt;commandName&gt;.dmp
///      • If the file exists it is reused as-is (fast, consistent across re-runs).
///      • Set DD_REFRESH_DUMPS=1 to force regeneration even when a cached dump exists.
///   2. ScenarioHost generation    ─  runs DumpDetective.ScenarioHost.exe &lt;commandName&gt;
///      in a child process; the host sets up the scenario state, captures a heap
///      dump of itself, writes it to the per-scenario path, and exits.
///   3. Shared dump fallback       ─  ScenarioFixture.SharedDumpPath (the single
///      combined dump captured by the collection fixture at test startup).
///
/// When ScenarioHost is used the dump captures ONLY the state for that one scenario
/// (no cross-contamination from other scenarios running in the same process).
/// This enables stronger assertions (e.g. exact type counts, specific type names).
///
/// Usage in a test class:
/// <code>
/// public sealed class HeapStatsTest(CommandContext&lt;HeapStatsScenario&gt; ctx)
///     : ScenarioTestBase&lt;HeapStatsScenario&gt;(ctx), IClassFixture&lt;CommandContext&lt;HeapStatsScenario&gt;&gt;
/// {
///     [Fact] public void TypeTable_HasRows() => DocAssert.TableHasMinRows(Doc, 5, "...");
/// }
/// </code>
/// </summary>
public sealed class CommandContext<TScenario> : IDisposable
    where TScenario : IScenario, new()
{
    /// <summary>The report document built from the dump — shared across all tests in the class.</summary>
    public ReportDoc Doc { get; }

    /// <summary>The scenario instance used to build and validate the report.</summary>
    public TScenario Scenario { get; } = new TScenario();

    /// <summary>
    /// Full path to the HTML file written by this fixture, or <c>null</c> if
    /// the report directory could not be created or the write failed.
    /// </summary>
    public string? HtmlReportPath { get; private set; }

    public CommandContext()
    {
        var scenario = Scenario;
        var cmd = CommandRegistry.Find(scenario.CommandName)
            ?? throw new InvalidOperationException(
                $"CommandRegistry.Find(\"{scenario.CommandName}\") returned null. " +
                $"Check that the command is registered in CommandRegistry.");

        var dumpPath = ResolveScenarioDump(scenario.CommandName);

        // Open, build, close — DumpContext is not kept open between tests
        using var ctx = DumpContext.Open(dumpPath);
        Doc = cmd.BuildReport(ctx);

        // Write the HTML report next to the dump, inside a Reports/ subfolder.
        // This survives cleanup because ScenarioFixture only deletes the .dmp file itself.
        try
        {
            var reportsDir = Path.Combine(Path.GetDirectoryName(dumpPath)!, "Reports");
            Directory.CreateDirectory(reportsDir);

            var dumpStamp = Path.GetFileNameWithoutExtension(dumpPath); // e.g. heap-stats or dd_test_…
            var fileName  = $"{dumpStamp}_{scenario.CommandName}.html";
            HtmlReportPath = Path.Combine(reportsDir, fileName);

            using var sink = new HtmlSink(HtmlReportPath);
            ReportDocReplay.Replay(Doc, sink);
        }
        catch
        {
            HtmlReportPath = null;
        }
    }

    // DumpContext is already disposed in the constructor — nothing to do here
    public void Dispose() { }

    // ── Dump resolution ───────────────────────────────────────────────────────

    private static string ResolveScenarioDump(string commandName)
    {
        // 1 — Per-scenario cached dump
        string scenariosDir = Path.Combine(SelfDumpCapture.DefaultDirectory, "Scenarios");
        string scenarioDump = Path.Combine(scenariosDir, $"{commandName}.dmp");

        bool forceRefresh = Environment.GetEnvironmentVariable("DD_REFRESH_DUMPS") == "1";

        // Also regenerate if the ScenarioHost exe is newer than the cached dump
        // (catches the common case: ScenarioHost source changed → binary updated → old dump is stale)
        if (!forceRefresh && File.Exists(scenarioDump))
        {
            var hostExeForFreshness = FindScenarioHostExe();
            if (hostExeForFreshness is not null)
            {
                var dumpTime = File.GetLastWriteTimeUtc(scenarioDump);
                var hostTime = File.GetLastWriteTimeUtc(hostExeForFreshness);
                if (hostTime > dumpTime)
                    forceRefresh = true;
            }
        }

        if (!forceRefresh && File.Exists(scenarioDump))
            return scenarioDump;

        // 2 — Try to generate via ScenarioHost
        var hostExe = FindScenarioHostExe();
        if (hostExe is not null)
        {
            Directory.CreateDirectory(scenariosDir);

            var psi = new ProcessStartInfo(hostExe, commandName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(TimeSpan.FromMinutes(2));

            if (proc.ExitCode == 0 && File.Exists(stdout))
                return stdout;

            // ScenarioHost reported failure — log to stderr and fall through
            string stderr = proc.StandardError.ReadToEnd().Trim();
            Console.Error.WriteLine(
                $"[CommandContext] ScenarioHost exited {proc.ExitCode} for '{commandName}'. " +
                $"stderr: {stderr}");
        }

        // 3 — Fall back to the shared combined dump
        return ScenarioFixture.SharedDumpPath
            ?? throw new InvalidOperationException(
                "No dump available: ScenarioHost not found and ScenarioFixture.SharedDumpPath is null. " +
                "Ensure the test class carries [Collection(\"Scenarios\")] so the " +
                "collection fixture is initialized before this class fixture.");
    }

    /// <summary>
    /// Locates DumpDetective.ScenarioHost.exe by searching:
    ///   1. Same directory as the test assembly (works when Tests.csproj has a build reference).
    ///   2. Standard relative build output paths from the test assembly directory.
    /// Returns <c>null</c> if not found.
    /// </summary>
    private static string? FindScenarioHostExe()
    {
        const string exeName = "DumpDetective.DiagnosticScenarios.exe";

        string testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // 1. Same output directory (when CopyToOutputDirectory is set)
        var candidate = Path.Combine(testDir, exeName);
        if (File.Exists(candidate)) return candidate;

        // 2. Sibling project: ../../../DumpDetective.ScenarioHost/bin/<config>/<tfm>/
        //    e.g. DumpDetective.Tests/bin/Debug/net10.0/ → ../../../DumpDetective.ScenarioHost/bin/Debug/net10.0/
        var config = new DirectoryInfo(testDir).Name;          // e.g. net10.0
        var build  = new DirectoryInfo(testDir).Parent?.Name;  // e.g. Debug
        if (config is not null && build is not null)
        {
            var sibling = Path.GetFullPath(
                Path.Combine(testDir, "..", "..", "..", "..",
                    "DumpDetective.DiagnosticScenarios", "bin", build, config, exeName));
            if (File.Exists(sibling)) return sibling;

            // Also try Release build
            var siblingRelease = Path.GetFullPath(
                Path.Combine(testDir, "..", "..", "..", "..",
                    "DumpDetective.DiagnosticScenarios", "bin", "Release", config, exeName));
            if (File.Exists(siblingRelease)) return siblingRelease;
        }

        return null;
    }
}

