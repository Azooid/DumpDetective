using DumpDetective.Analysis.Trace.BuiltInClassifiers;
using DumpDetective.Core.Tracing;
using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Centralises all ETW / EventPipe event classification via a two-tier classifier pipeline.
///
/// <para><b>Tier 1 — Plugin classifiers</b> (<see cref="Register"/>):
/// run first, in reverse-registration order.  Plugins are loaded by
/// <c>PluginLoader</c>; each returns <see cref="TraceEventKind.Unknown"/> for
/// events it does not own.</para>
///
/// <para><b>Tier 2 — Built-in classifiers</b> (<c>_builtIn</c>):
/// domain-scoped, immutable at startup.  Each classifier handles exactly one
/// concern (GC, JIT, Threading, HTTP …) and is independent of every other.</para>
///
/// <para>Adding a new domain classifier: create a class in
/// <c>BuiltInClassifiers/</c> that implements <see cref="ITraceEventClassifier"/>
/// (optionally <see cref="IProviderScopedClassifier"/>), then add one instance to
/// the <c>_builtIn</c> array below.  No other file needs to change.</para>
///
/// <para>Phase 3 note: <see cref="IProviderScopedClassifier.Providers"/> metadata
/// is already declared on every built-in classifier and can be used to build a
/// provider-keyed dispatch table, skipping irrelevant classifiers per event.
/// The interface is declared; the optimisation is not yet active.</para>
/// </summary>
public static class EventNormalizer
{
    // ── Built-in classifiers (ordered; KestrelClassifier before HttpClassifier) ──

    private static readonly ITraceEventClassifier[] _builtIn =
    [
        new GcClassifier(),
        new ExceptionClassifier(),
        new ContentionClassifier(),
        new JitClassifier(),
        new ThreadingClassifier(),
        new FileIoClassifier(),
        new AssemblyClassifier(),
        new ProcessClassifier(),
        new DnsClassifier(),
        new SocketClassifier(),
        new SqlClassifier(),
        new KestrelClassifier(),    // must precede HttpClassifier
        new HttpClassifier(),
        new ActivityClassifier(),   // broad Activity matching — last
    ];

    // ── Plugin classifier registry ────────────────────────────────────────────

    // Immutable snapshot updated via CAS — no locks on the read path.
    private static volatile ITraceEventClassifier[] _plugins = [];

    /// <summary>
    /// Registers a plugin <see cref="ITraceEventClassifier"/>.  Plugin classifiers
    /// run before the built-in classifiers, in reverse-registration order
    /// (last registered = highest priority).
    ///
    /// <para>All registrations must complete <b>before</b> the first trace pass.
    /// Do not register classifiers while a trace is being processed.</para>
    /// </summary>
    public static void Register(ITraceEventClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        // Prepend with CAS so the last-registered classifier has the highest priority.
        ITraceEventClassifier[] prev, next;
        do
        {
            prev = _plugins;
            next = [classifier, .. prev];
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref _plugins, next, prev), prev));
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a raw trace event into a <see cref="TraceEventKind"/>.
    /// Returns <see cref="TraceEventKind.Unknown"/> for unrecognised events.
    /// </summary>
    public static TraceEventKind Classify(TraceEvent ev)
    {
        string name     = ev.EventName    ?? "";
        string provider = ev.ProviderName ?? "";
        if (name.Length == 0) return TraceEventKind.Unknown;

        // Special case: .nettrace WaitHandleWait events appear as bare "Wait" (opcode not in name).
        // Use ev.Opcode to disambiguate start vs stop; fall back to WaitHandleWaitStart when opcode is unknown.
        if (name.Equals("Wait", StringComparison.OrdinalIgnoreCase) &&
            provider.Contains("DotNETRuntime", StringComparison.OrdinalIgnoreCase))
        {
            return ev.Opcode == TraceEventOpcode.Stop ? TraceEventKind.WaitHandleWaitStop
                                                      : TraceEventKind.WaitHandleWaitStart;
        }

        // Volatile read — gets the current immutable snapshot without a lock.
        foreach (var c in _plugins)
        {
            var k = c.Classify(provider, name);
            if (!k.IsUnknown) return k;
        }

        return ClassifyBuiltIn(name, provider);
    }

    // ── Built-in classification ───────────────────────────────────────────────

    private static TraceEventKind ClassifyBuiltIn(string name, string provider)
    {
        foreach (var c in _builtIn)
        {
            var k = c.Classify(provider, name);
            if (!k.IsUnknown) return k;
        }
        return TraceEventKind.Unknown;
    }

}