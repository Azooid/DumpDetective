using DumpDetective.Core.Models;

namespace DumpDetective.Tests.Fixtures;

/// <summary>
/// Represents a single named diagnostic scenario used by integration tests.
///
/// Lifecycle (managed by <see cref="ScenarioFixture"/>):
///   1. <see cref="Setup"/>  — called once before the shared dump is captured.
///   2. Dump is written.
///   3. Each test opens the dump and calls <see cref="Validate"/>.
///   4. <see cref="Teardown"/> — called once after all tests complete.
///
/// Env vars that affect the fixture:
///   DD_TEST_DUMP     — path to a pre-existing dump; skips capture and Teardown.
///   DD_KEEP_DUMPS=1  — keep the captured dump on disk after the run.
///   DD_TEST_DUMP_DIR — directory in which to write the auto-captured dump.
/// </summary>
public interface IScenario
{
    /// <summary>
    /// Matches the <c>ICommand.Name</c> that should analyse this scenario,
    /// e.g. <c>"heap-stats"</c>.
    /// </summary>
    string CommandName { get; }

    /// <summary>One-line description shown in xUnit test output.</summary>
    string Description { get; }

    /// <summary>
    /// <see langword="false"/> for scenarios whose <see cref="Setup"/> would
    /// consume the test-runner's own thread pool or create unresolvable deadlocks
    /// (e.g. <c>thread-pool-starvation</c>, <c>deadlock-detection</c>).
    /// <see cref="Setup"/> is <b>not</b> called for these; the command still runs
    /// against an empty-state dump and <see cref="Validate"/> should be lenient.
    /// </summary>
    bool SafeInProcess => true;

    /// <summary>
    /// Allocates objects / starts background activity that the command should
    /// detect in the dump. Must not block indefinitely.
    /// Called exactly once before the shared dump is captured.
    /// </summary>
    void Setup();

    /// <summary>
    /// Asserts expected structural properties of the <see cref="ReportDoc"/>
    /// produced by the command. Use <c>&gt;=</c> rather than <c>==</c> because
    /// the dump contains artefacts from every other scenario.
    /// </summary>
    void Validate(ReportDoc doc);

    /// <summary>
    /// Releases any resources held by <see cref="Setup"/> (signal thread gates,
    /// dispose timers, etc.). Called after all tests, regardless of pass/fail.
    /// Default implementation is a no-op.
    /// </summary>
    void Teardown() { }
}
