using DumpDetective.Tests.Fixtures;
using DumpDetective.Tests.Scenarios.Async;
using DumpDetective.Tests.Scenarios.Exceptions;
using DumpDetective.Tests.Scenarios.Gc;
using DumpDetective.Tests.Scenarios.Heap;
using DumpDetective.Tests.Scenarios.Leaks;
using DumpDetective.Tests.Scenarios.Thread;
using DumpDetective.Tests.Scenarios.Types;

namespace DumpDetective.Tests.Scenarios;

/// <summary>
/// Single source of truth for all registered <see cref="IScenario"/> instances.
/// Adding a new scenario: add one entry to <see cref="All"/> only.
/// </summary>
public static class AllScenarios
{
    public static readonly IReadOnlyList<IScenario> All =
    [
        // ── Heap & Memory ──────────────────────────────────────────────────────
        new HeapStatsScenario(),
        new GenSummaryScenario(),
        new LargeObjectsScenario(),
        new MemoryLeakScenario(),
        new HeapFragmentationScenario(),
        new HighRefsScenario(),
        new StringDuplicatesScenario(),

        // ── GC / Handles ───────────────────────────────────────────────────────
        new PinnedObjectsScenario(),
        new FinalizerQueueScenario(),
        new HandleTableScenario(),
        new StaticRefsScenario(),
        new WeakRefsScenario(),

        // ── Threads ────────────────────────────────────────────────────────────
        new ThreadAnalysisScenario(),
        new ThreadPoolScenario(),        // SafeInProcess = false — setup skipped
        new DeadlockScenario(),          // SafeInProcess = false — setup skipped

        // ── Async ──────────────────────────────────────────────────────────────
        new AsyncStacksScenario(),
        new HttpRequestsScenario(),

        // ── Exceptions & Events ────────────────────────────────────────────────
        new ExceptionAnalysisScenario(),
        new EventAnalysisScenario(),

        // ── Leaks ──────────────────────────────────────────────────────────────
        new TimerLeaksScenario(),
        new ConnectionPoolScenario(),
        new WcfChannelsScenario(),

        // ── Types & Modules ────────────────────────────────────────────────────
        new ModuleListScenario(),
        new TypeInstancesScenario(),
    ];

    /// <summary>
    /// Scenarios whose <see cref="IScenario.Setup"/> is safe to run in the test
    /// process (won't stall the thread pool or create unresolvable deadlocks).
    /// </summary>
    public static IEnumerable<IScenario> Safe => All.Where(s => s.SafeInProcess);
}
