using DumpDetective.DiagnosticScenarios.Scenarios.Async;
using DumpDetective.DiagnosticScenarios.Scenarios.Exceptions;
using DumpDetective.DiagnosticScenarios.Scenarios.Gc;
using DumpDetective.DiagnosticScenarios.Scenarios.Heap;
using DumpDetective.DiagnosticScenarios.Scenarios.Leaks;
using DumpDetective.DiagnosticScenarios.Scenarios.Thread;
using DumpDetective.DiagnosticScenarios.Scenarios.Types;

namespace DumpDetective.DiagnosticScenarios;

public static class ScenarioList
{
    public static IReadOnlyList<IScenario> All =>
    [
        new HeapStatsScenario(),
        new GenSummaryScenario(),
        new LargeObjectsScenario(),
        new MemoryLeakScenario(),
        new HeapFragmentationScenario(),
        new HighRefsScenario(),
        new StringDuplicatesScenario(),
        new PinnedObjectsScenario(),
        new FinalizerQueueScenario(),
        new HandleTableScenario(),
        new StaticRefsScenario(),
        new WeakRefsScenario(),
        new ThreadAnalysisScenario(),
        new ThreadPoolScenario(),
        new DeadlockScenario(),
        new AsyncStacksScenario(),
        new HttpRequestsScenario(),
        new ExceptionAnalysisScenario(),
        new EventAnalysisScenario(),
        new TimerLeaksScenario(),
        new ConnectionPoolScenario(),
        new WcfChannelsScenario(),
        new ModuleListScenario(),
        new TypeInstancesScenario(),
    ];

    public static IEnumerable<IScenario> Safe => All.Where(s => s.SafeInProcess);
}
