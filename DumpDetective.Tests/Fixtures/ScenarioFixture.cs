using DumpDetective.Reporting;
using DumpDetective.Tests.Scenarios;

namespace DumpDetective.Tests.Fixtures;

/// <summary>
/// xUnit collection-fixture that owns the shared dump used by all scenario tests.
///
/// Lifecycle per test run:
///   1. <c>InitializeAsync</c>  — run Setup() on every safe scenario, then capture dump.
///   2. All <c>[Collection("Scenarios")]</c> tests execute (reading the dump in parallel).
///   3. <c>DisposeAsync</c>     — run Teardown() on every safe scenario; optionally delete dump.
///
/// Env vars:
///   DD_TEST_DUMP     — path to an existing dump; bypasses capture and Teardown.
///   DD_KEEP_DUMPS=1  — keep the auto-captured dump after the run.
///   DD_TEST_DUMP_DIR — directory for the auto-captured dump (default: %TEMP%\DumpDetective).
/// </summary>
public sealed class ScenarioFixture : IAsyncLifetime
{
    /// <summary>
    /// Set once during <see cref="InitializeAsync"/>. Read by
    /// <see cref="CommandContext{TScenario}"/> class fixtures, which cannot receive
    /// collection fixtures via constructor injection.
    /// xUnit guarantees collection fixtures are fully initialized before any class
    /// fixture or test constructor runs.
    /// </summary>
    internal static volatile string? SharedDumpPath;

    private bool _dumpOwnedByFixture;

    /// <summary>Full path to the dump file used by all tests in this collection.</summary>
    public string DumpPath { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // Wire up sinks (idempotent — safe to call multiple times)
        ReportingBootstrap.Register();

        var externalDump = Environment.GetEnvironmentVariable("DD_TEST_DUMP");
        if (!string.IsNullOrWhiteSpace(externalDump))
        {
            if (!File.Exists(externalDump))
                throw new FileNotFoundException(
                    $"DD_TEST_DUMP points to a file that does not exist: '{externalDump}'");

            DumpPath = externalDump;
            SharedDumpPath = DumpPath;
            _dumpOwnedByFixture = false;
            return;
        }

        // Run every safe scenario's Setup() before we capture
        foreach (var scenario in AllScenarios.Safe)
            scenario.Setup();

        // Small yield so finalizers and async state machines settle
        await Task.Delay(50);

        var dumpDir = Environment.GetEnvironmentVariable("DD_TEST_DUMP_DIR")
                      ?? SelfDumpCapture.DefaultDirectory;

        DumpPath = SelfDumpCapture.Write(dumpDir);
        SharedDumpPath = DumpPath;
        _dumpOwnedByFixture = true;
    }

    public Task DisposeAsync()
    {
        // Only call Teardown when we set up the scenarios ourselves
        if (_dumpOwnedByFixture)
        {
            foreach (var scenario in AllScenarios.Safe)
            {
                try { scenario.Teardown(); }
                catch { /* best-effort — never fail Dispose */ }
            }

            bool keepDump = Environment.GetEnvironmentVariable("DD_KEEP_DUMPS") == "1";
            if (!keepDump && File.Exists(DumpPath))
                File.Delete(DumpPath);
        }

        SharedDumpPath = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// xUnit collection definition — all tests in <c>[Collection("Scenarios")]</c>
/// share one <see cref="ScenarioFixture"/> instance.
/// </summary>
[CollectionDefinition("Scenarios")]
public sealed class ScenarioCollection : ICollectionFixture<ScenarioFixture> { }
