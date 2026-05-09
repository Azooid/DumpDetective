namespace DumpDetective.Core.Models.CommandData;

/// <summary>
/// Results from context-switch (CSwitch) kernel event analysis.
///
/// CSwitch events fire every time the OS scheduler switches a CPU from one thread
/// to another. They reveal:
///   • Which threads run most frequently (scheduling rate)
///   • Whether a thread was preempted (quantum expired / higher-priority ready) or
///     voluntarily blocked (I/O, lock, sleep, thread-pool queue)
///   • Which kernel wait-reason dominates — distinguishing lock contention from
///     I/O waits from timer sleeps
///   • How long each thread's CPU run-slices are (short slices → spinning / high-contention)
///
/// Collection (requires kernel events):
///   PerfView:  /KernelEvents:ContextSwitch
///   xperf:     xperf -on CSWITCH
/// </summary>
public sealed record ContextSwitchTraceData(
    string  TraceInfo,
    string? FilteredProcess,

    // ── Aggregate counts ───────────────────────────────────────────────────
    long   TotalSwitches,
    long   VoluntarySwitches,    // OldThreadState = 5 (Waiting)
    long   PreemptedSwitches,    // OldThreadState = 1 (Ready / preempted)
    double SwitchesPerSecond,
    double VoluntaryPct,
    double PreemptedPct,

    // ── Idle thread (Windows Idle process, PID 0) ───────────────────────
    // Idle switches = times a real thread woke up and displaced the CPU idle state.
    // IdleRunMs = total wall time the CPU was doing no real work.
    long   IdleSwitches,
    double IdleRunMs,

    // ── Duration span ──────────────────────────────────────────────────────
    double TraceSpanMs,

    // ── Per-thread summary ─────────────────────────────────────────────────
    IReadOnlyList<CswitchThreadSummary> TopThreads,

    // ── Wait reason distribution ───────────────────────────────────────────
    IReadOnlyList<WaitReasonSummary> WaitReasons,

    // ── Per-second switch-rate timeline ───────────────────────────────────
    IReadOnlyList<double>? Timeline,

    bool HasData);

/// <summary>Per-thread scheduling statistics derived from CSwitch events.</summary>
public sealed record CswitchThreadSummary(
    string ProcessName,
    int    ThreadId,
    long   SwitchCount,
    long   VoluntaryCount,
    long   PreemptedCount,
    double TotalRunMs,
    double AvgRunSliceMs,    // TotalRunMs / SwitchCount — length of each CPU burst
    double PctOfAllSwitches);

/// <summary>Wait-reason distribution — why threads voluntarily give up the CPU.</summary>
public sealed record WaitReasonSummary(
    string ReasonName,
    long   Count,
    double Pct);
