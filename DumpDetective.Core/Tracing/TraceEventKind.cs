using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DumpDetective.Core.Tracing;

/// <summary>
/// Identifies a normalised trace event kind.
///
/// <para>Unlike an enum, new kinds can be registered at runtime by plugins via
/// <see cref="Register"/>, enabling the classification pipeline to be extended
/// without recompiling core assemblies.</para>
///
/// <para>Instances are <b>singletons</b> — equality is reference equality (O(1)).
/// The <c>==</c> / <c>!=</c> operators use <see cref="object.ReferenceEquals"/>.</para>
///
/// <para>Built-in kinds (same names as the former enum) are declared as
/// <c>static readonly</c> fields below.  Use <c>using static</c> to access them
/// without the type prefix.</para>
/// </summary>
public sealed class TraceEventKind : IEquatable<TraceEventKind>
{
    // ── Registry ─────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, TraceEventKind> _registry =
        new(StringComparer.Ordinal);

    /// <summary>Human-readable name, e.g. <c>"CpuSample"</c>.</summary>
    public string Name { get; }

    /// <summary><c>true</c> when this is the sentinel <see cref="Unknown"/> instance.</summary>
    public bool IsUnknown => ReferenceEquals(this, Unknown);

    private TraceEventKind(string name) => Name = name;

    /// <summary>
    /// Creates a built-in kind during static initialisation (no lock needed — type
    /// initialisation is single-threaded).
    /// </summary>
    private static TraceEventKind BuiltIn(string name)
    {
        var k = new TraceEventKind(name);
        _registry[name] = k;
        return k;
    }

    /// <summary>
    /// Registers a new kind by name and returns it.  If a kind with this name
    /// already exists (built-in or previously registered) the existing instance
    /// is returned unchanged.  Lock-free and thread-safe.
    /// </summary>
    public static TraceEventKind Register(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Kind name must be non-empty.", nameof(name));

        // GetOrAdd may invoke the factory more than once under contention, but only
        // one instance is ever stored and returned — reference equality stays intact.
        return _registry.GetOrAdd(name, static n => new TraceEventKind(n));
    }

    /// <summary>Returns the kind for <paramref name="name"/>, or <c>null</c> if not registered.</summary>
    public static TraceEventKind? Find(string name)
        => _registry.TryGetValue(name, out var k) ? k : null;

    /// <summary>Returns a snapshot of all registered kinds (built-in + plugin-registered).</summary>
    public static IReadOnlyCollection<TraceEventKind> All => [.. _registry.Values];

    // ── Equality — reference equality (instances are singletons) ─────────────

    public bool   Equals(TraceEventKind? other) => ReferenceEquals(this, other);
    public override bool   Equals(object? obj)  => ReferenceEquals(this, obj);
    public override int    GetHashCode()         => RuntimeHelpers.GetHashCode(this);
    public override string ToString()            => Name;

    public static bool operator ==(TraceEventKind? a, TraceEventKind? b) => ReferenceEquals(a, b);
    public static bool operator !=(TraceEventKind? a, TraceEventKind? b) => !ReferenceEquals(a, b);

    // ── Built-in kinds (same names as the former enum) ────────────────────────
    // NOTE: order of field initialisation matters — Unknown must be first.

    public static readonly TraceEventKind Unknown = BuiltIn("Unknown");

    // ── CPU / Sampling ───────────────────────────────────────────────────────
    public static readonly TraceEventKind CpuSample = BuiltIn("CpuSample");

    // ── GC ───────────────────────────────────────────────────────────────────
    public static readonly TraceEventKind GCStart            = BuiltIn("GCStart");
    public static readonly TraceEventKind GCStop             = BuiltIn("GCStop");
    public static readonly TraceEventKind GCSuspendEEStart   = BuiltIn("GCSuspendEEStart");
    public static readonly TraceEventKind GCSuspendEEStop    = BuiltIn("GCSuspendEEStop");
    public static readonly TraceEventKind GCRestartEEStart   = BuiltIn("GCRestartEEStart");
    public static readonly TraceEventKind GCRestartEEStop    = BuiltIn("GCRestartEEStop");
    public static readonly TraceEventKind GCHeapStats        = BuiltIn("GCHeapStats");
    public static readonly TraceEventKind GCAllocationTick   = BuiltIn("GCAllocationTick");
    public static readonly TraceEventKind GCFinalizeObject   = BuiltIn("GCFinalizeObject");

    // ── Contention / Locks ───────────────────────────────────────────────────
    public static readonly TraceEventKind ContentionStart = BuiltIn("ContentionStart");
    public static readonly TraceEventKind ContentionStop  = BuiltIn("ContentionStop");

    // ── Exceptions ───────────────────────────────────────────────────────────
    public static readonly TraceEventKind ExceptionThrown      = BuiltIn("ExceptionThrown");
    public static readonly TraceEventKind ExceptionCatchStart  = BuiltIn("ExceptionCatchStart");
    public static readonly TraceEventKind ExceptionCatchStop   = BuiltIn("ExceptionCatchStop");
    // ExceptionHandled = Exception/Stop (.NET Framework: exception handling complete)
    public static readonly TraceEventKind ExceptionHandled     = BuiltIn("ExceptionHandled");
    // ExceptionFinally = .NET Framework exception finally block events
    public static readonly TraceEventKind ExceptionFinallyStart = BuiltIn("ExceptionFinallyStart");
    public static readonly TraceEventKind ExceptionFinallyStop  = BuiltIn("ExceptionFinallyStop");

    // ── ThreadPool ───────────────────────────────────────────────────────────
    public static readonly TraceEventKind ThreadPoolEnqueue    = BuiltIn("ThreadPoolEnqueue");
    public static readonly TraceEventKind ThreadPoolDequeue    = BuiltIn("ThreadPoolDequeue");
    public static readonly TraceEventKind ThreadPoolAdjustment = BuiltIn("ThreadPoolAdjustment");
    public static readonly TraceEventKind WaitHandleWaitStart  = BuiltIn("WaitHandleWaitStart");
    public static readonly TraceEventKind WaitHandleWaitStop   = BuiltIn("WaitHandleWaitStop");

    // ── JIT ──────────────────────────────────────────────────────────────────
    public static readonly TraceEventKind JitMethodStart      = BuiltIn("JitMethodStart");
    public static readonly TraceEventKind JitMethodLoad       = BuiltIn("JitMethodLoad");
    public static readonly TraceEventKind JitInliningSucceeded = BuiltIn("JitInliningSucceeded");
    public static readonly TraceEventKind JitInliningFailed    = BuiltIn("JitInliningFailed");

    // ── HTTP ─────────────────────────────────────────────────────────────────
    public static readonly TraceEventKind HttpRequestStart          = BuiltIn("HttpRequestStart");
    public static readonly TraceEventKind HttpRequestStop           = BuiltIn("HttpRequestStop");
    public static readonly TraceEventKind HttpClientGetRequestStart  = BuiltIn("HttpClientGetRequestStart");
    public static readonly TraceEventKind HttpClientGetRequestStop   = BuiltIn("HttpClientGetRequestStop");
    public static readonly TraceEventKind HttpClientGetResponseStart = BuiltIn("HttpClientGetResponseStart");
    public static readonly TraceEventKind HttpClientGetResponseStop  = BuiltIn("HttpClientGetResponseStop");

    // ── TPL / Async ──────────────────────────────────────────────────────────
    public static readonly TraceEventKind TaskScheduled      = BuiltIn("TaskScheduled");
    public static readonly TraceEventKind TaskStarted        = BuiltIn("TaskStarted");
    public static readonly TraceEventKind TaskCompleted      = BuiltIn("TaskCompleted");
    public static readonly TraceEventKind TaskWaitBegin      = BuiltIn("TaskWaitBegin");
    public static readonly TraceEventKind TaskWaitEnd        = BuiltIn("TaskWaitEnd");
    public static readonly TraceEventKind AwaiterContinuation = BuiltIn("AwaiterContinuation");

    // ── SQL / ADO.NET ────────────────────────────────────────────────────────
    public static readonly TraceEventKind SqlCommandStart   = BuiltIn("SqlCommandStart");
    public static readonly TraceEventKind SqlCommandStop    = BuiltIn("SqlCommandStop");
    public static readonly TraceEventKind SqlConnectionOpen  = BuiltIn("SqlConnectionOpen");
    public static readonly TraceEventKind SqlConnectionClose = BuiltIn("SqlConnectionClose");

    // ── Process / Thread ─────────────────────────────────────────────────────
    public static readonly TraceEventKind ProcessStart        = BuiltIn("ProcessStart");
    public static readonly TraceEventKind ProcessStop         = BuiltIn("ProcessStop");
    public static readonly TraceEventKind ThreadContextSwitch = BuiltIn("ThreadContextSwitch");

    // ── File I/O (ETL only — Microsoft-Windows-Kernel-File) ──────────────────
    public static readonly TraceEventKind FileRead   = BuiltIn("FileRead");
    public static readonly TraceEventKind FileWrite  = BuiltIn("FileWrite");
    public static readonly TraceEventKind FileCreate = BuiltIn("FileCreate");
    public static readonly TraceEventKind FileClose  = BuiltIn("FileClose");
    public static readonly TraceEventKind FileFlush  = BuiltIn("FileFlush");

    // ── Sockets — System.Net.Sockets EventSource (.NET 5+) ───────────────────
    public static readonly TraceEventKind SocketConnectStart  = BuiltIn("SocketConnectStart");
    public static readonly TraceEventKind SocketConnectStop   = BuiltIn("SocketConnectStop");
    public static readonly TraceEventKind SocketConnectFailed = BuiltIn("SocketConnectFailed");
    public static readonly TraceEventKind SocketSendStart     = BuiltIn("SocketSendStart");
    public static readonly TraceEventKind SocketSendStop      = BuiltIn("SocketSendStop");
    public static readonly TraceEventKind SocketReceiveStart  = BuiltIn("SocketReceiveStart");
    public static readonly TraceEventKind SocketReceiveStop   = BuiltIn("SocketReceiveStop");

    // ── DNS — System.Net.NameResolution EventSource ───────────────────────────
    public static readonly TraceEventKind DnsResolutionStart  = BuiltIn("DnsResolutionStart");
    public static readonly TraceEventKind DnsResolutionStop   = BuiltIn("DnsResolutionStop");
    public static readonly TraceEventKind DnsResolutionFailed = BuiltIn("DnsResolutionFailed");

    // ── Kestrel — Microsoft-AspNetCore-Server-Kestrel ─────────────────────────
    public static readonly TraceEventKind KestrelConnectionStart    = BuiltIn("KestrelConnectionStart");
    public static readonly TraceEventKind KestrelConnectionStop     = BuiltIn("KestrelConnectionStop");
    public static readonly TraceEventKind KestrelConnectionRejected = BuiltIn("KestrelConnectionRejected");
    public static readonly TraceEventKind KestrelRequestError       = BuiltIn("KestrelRequestError");
    public static readonly TraceEventKind KestrelConnectionQueueStart = BuiltIn("KestrelConnectionQueueStart");
    public static readonly TraceEventKind KestrelConnectionQueueStop  = BuiltIn("KestrelConnectionQueueStop");

    // ── GC Handles ───────────────────────────────────────────────────────────
    public static readonly TraceEventKind GCHandleCreated   = BuiltIn("GCHandleCreated");
    public static readonly TraceEventKind GCHandleDestroyed = BuiltIn("GCHandleDestroyed");

    // ── ASP.NET Core pipeline ─────────────────────────────────────────────────
    public static readonly TraceEventKind AspNetCoreRouteMatched = BuiltIn("AspNetCoreRouteMatched");
    public static readonly TraceEventKind AspNetCoreAuthStart    = BuiltIn("AspNetCoreAuthStart");
    public static readonly TraceEventKind AspNetCoreAuthStop     = BuiltIn("AspNetCoreAuthStop");
    public static readonly TraceEventKind AspNetCoreAuthFailed   = BuiltIn("AspNetCoreAuthFailed");

    // ── OpenTelemetry / DiagnosticSource Activity ─────────────────────────────
    public static readonly TraceEventKind ActivityStart = BuiltIn("ActivityStart");
    public static readonly TraceEventKind ActivityStop  = BuiltIn("ActivityStop");

    // ── GC extended ──────────────────────────────────────────────────────────
    public static readonly TraceEventKind GCFinalizersStart    = BuiltIn("GCFinalizersStart");
    public static readonly TraceEventKind GCFinalizersStop     = BuiltIn("GCFinalizersStop");
    public static readonly TraceEventKind GCTriggered          = BuiltIn("GCTriggered");
    public static readonly TraceEventKind GCCreateSegment      = BuiltIn("GCCreateSegment");
    public static readonly TraceEventKind GCCommittedUsage     = BuiltIn("GCCommittedUsage");
    public static readonly TraceEventKind GCGlobalHeapHistory  = BuiltIn("GCGlobalHeapHistory");
    public static readonly TraceEventKind GCPerHeapHistory     = BuiltIn("GCPerHeapHistory");
    public static readonly TraceEventKind GCGenerationRange    = BuiltIn("GCGenerationRange");
    public static readonly TraceEventKind GCPinObjectAtGCTime  = BuiltIn("GCPinObjectAtGCTime");
    public static readonly TraceEventKind GCSetGCHandle        = BuiltIn("GCSetGCHandle");
    public static readonly TraceEventKind GCMarkWithType       = BuiltIn("GCMarkWithType");
    public static readonly TraceEventKind GCJoin               = BuiltIn("GCJoin");
    public static readonly TraceEventKind GCBulkData           = BuiltIn("GCBulkData");

    // ── ThreadPool worker lifecycle ───────────────────────────────────────────
    public static readonly TraceEventKind ThreadPoolWorkerStart       = BuiltIn("ThreadPoolWorkerStart");
    public static readonly TraceEventKind ThreadPoolWorkerStop        = BuiltIn("ThreadPoolWorkerStop");
    public static readonly TraceEventKind ThreadPoolWorkerWait        = BuiltIn("ThreadPoolWorkerWait");
    public static readonly TraceEventKind ThreadPoolAdjustmentSample  = BuiltIn("ThreadPoolAdjustmentSample");
    public static readonly TraceEventKind ThreadPoolAdjustmentStats   = BuiltIn("ThreadPoolAdjustmentStats");

    // ── TPL task execution ────────────────────────────────────────────────────
    public static readonly TraceEventKind TaskExecuteStart        = BuiltIn("TaskExecuteStart");
    public static readonly TraceEventKind TaskExecuteStop         = BuiltIn("TaskExecuteStop");
    public static readonly TraceEventKind TaskScheduledSend       = BuiltIn("TaskScheduledSend");
    public static readonly TraceEventKind TaskWaitSend            = BuiltIn("TaskWaitSend");
    public static readonly TraceEventKind TraceSynchronousWorkStart = BuiltIn("TraceSynchronousWorkStart");
    public static readonly TraceEventKind TraceSynchronousWorkStop  = BuiltIn("TraceSynchronousWorkStop");
    // TraceOperation = async operation lifecycle (TplEventSource, .NET Core)
    public static readonly TraceEventKind TraceOperationStart     = BuiltIn("TraceOperationStart");
    public static readonly TraceEventKind TraceOperationStop      = BuiltIn("TraceOperationStop");

    // ── Kestrel HTTP & TLS ────────────────────────────────────────────────────
    public static readonly TraceEventKind KestrelRequestStart      = BuiltIn("KestrelRequestStart");
    public static readonly TraceEventKind KestrelRequestStop       = BuiltIn("KestrelRequestStop");
    public static readonly TraceEventKind KestrelTlsHandshakeStart = BuiltIn("KestrelTlsHandshakeStart");
    public static readonly TraceEventKind KestrelTlsHandshakeStop  = BuiltIn("KestrelTlsHandshakeStop");

    // ── System.Net.Http connection lifecycle ──────────────────────────────────
    public static readonly TraceEventKind HttpConnectionEstablished  = BuiltIn("HttpConnectionEstablished");
    public static readonly TraceEventKind HttpConnectionClosed       = BuiltIn("HttpConnectionClosed");
    public static readonly TraceEventKind HttpRequestLeftQueue       = BuiltIn("HttpRequestLeftQueue");
    public static readonly TraceEventKind HttpRequestContentStart    = BuiltIn("HttpRequestContentStart");
    public static readonly TraceEventKind HttpRequestContentStop     = BuiltIn("HttpRequestContentStop");
    public static readonly TraceEventKind HttpRequestHeadersStart    = BuiltIn("HttpRequestHeadersStart");
    public static readonly TraceEventKind HttpRequestHeadersStop     = BuiltIn("HttpRequestHeadersStop");
    public static readonly TraceEventKind HttpResponseHeadersStart   = BuiltIn("HttpResponseHeadersStart");
    public static readonly TraceEventKind HttpResponseHeadersStop    = BuiltIn("HttpResponseHeadersStop");

    // ── Socket accept ─────────────────────────────────────────────────────────
    public static readonly TraceEventKind SocketAcceptStart = BuiltIn("SocketAcceptStart");
    public static readonly TraceEventKind SocketAcceptStop  = BuiltIn("SocketAcceptStop");

    // ── Assembly / Module loading ─────────────────────────────────────────────
    public static readonly TraceEventKind AssemblyLoaded                     = BuiltIn("AssemblyLoaded");
    public static readonly TraceEventKind ModuleLoaded                       = BuiltIn("ModuleLoaded");
    public static readonly TraceEventKind AssemblyLoaderKnownPathProbed      = BuiltIn("AssemblyLoaderKnownPathProbed");
    public static readonly TraceEventKind AssemblyLoaderResolutionAttempted  = BuiltIn("AssemblyLoaderResolutionAttempted");
    public static readonly TraceEventKind AssemblyLoaderStart                = BuiltIn("AssemblyLoaderStart");
    public static readonly TraceEventKind AssemblyLoaderStop                 = BuiltIn("AssemblyLoaderStop");

    // ── Thread lifecycle ──────────────────────────────────────────────────────
    public static readonly TraceEventKind ThreadCreating = BuiltIn("ThreadCreating");
    public static readonly TraceEventKind ThreadRunning  = BuiltIn("ThreadRunning");

    // ── Tiered Compilation ────────────────────────────────────────────────────
    public static readonly TraceEventKind TieredCompilationBackgroundStart = BuiltIn("TieredCompilationBackgroundStart");
    public static readonly TraceEventKind TieredCompilationBackgroundStop  = BuiltIn("TieredCompilationBackgroundStop");
    public static readonly TraceEventKind TieredCompilationPause           = BuiltIn("TieredCompilationPause");
    public static readonly TraceEventKind TieredCompilationResume          = BuiltIn("TieredCompilationResume");

    // ── Type loading ──────────────────────────────────────────────────────────
    public static readonly TraceEventKind TypeLoadStart = BuiltIn("TypeLoadStart");
    public static readonly TraceEventKind TypeLoadStop  = BuiltIn("TypeLoadStop");

    // ── JIT extended ─────────────────────────────────────────────────────────
    public static readonly TraceEventKind JitR2RGetEntryPoint  = BuiltIn("JitR2RGetEntryPoint");
    public static readonly TraceEventKind JitMethodUnload      = BuiltIn("JitMethodUnload");
    public static readonly TraceEventKind JitMemoryAllocated   = BuiltIn("JitMemoryAllocated");
    public static readonly TraceEventKind JitMethodDetails     = BuiltIn("JitMethodDetails");
    public static readonly TraceEventKind JitTailCallSucceeded = BuiltIn("JitTailCallSucceeded");
}
