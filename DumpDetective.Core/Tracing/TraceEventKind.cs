namespace DumpDetective.Core.Tracing;

/// <summary>
/// Normalized event kind for all supported trace event types.
/// Used by <see cref="DumpDetective.Analysis.Trace.EventNormalizer"/> to classify
/// raw <c>TraceEvent</c> instances without scattering string-matching logic
/// across every analyzer.
///
/// Event name sources (as seen in real ETW / EventPipe traces):
///   CPU       Windows Kernel/PerfInfo/Sample · Microsoft-Windows-DotNETRuntime/SampledProfile
///   GC        Microsoft-Windows-DotNETRuntime/GC/Start|Stop|SuspendEEStart|SuspendEEStop|
///             RestartEEStart|RestartEEStop|HeapStats|AllocationTick|FinalizeObject
///   Contention Microsoft-Windows-DotNETRuntime/Contention/Start|Stop
///   Exception  Microsoft-Windows-DotNETRuntime/Exception/Start · ExceptionCatch/Start|Stop
///   ThreadPool Microsoft-Windows-DotNETRuntime/ThreadPool/Enqueue|Dequeue
///             · System.Diagnostics.Eventing.FrameworkEventSource/ThreadPoolEnqueueWork|DequeueWork
///             · ThreadPoolWorkerThreadAdjustment
///   JIT        Microsoft-Windows-DotNETRuntime/Method/JittingStarted|LoadVerbose|
///             InliningSucceeded|InliningFailedAnsi
///   HTTP       Microsoft-Windows-ASPNET/Request/Start|Stop
///             · AspNetTrace/AspNetReq/Start|Stop
///             · Microsoft-AspNetCore-Hosting/Request/Start|Stop (EventPipe)
///             · System.Diagnostics.Eventing.FrameworkEventSource/GetResponse/Start|Stop
///             · System.Diagnostics.Eventing.FrameworkEventSource/GetRequestStream/Start|Stop
///   SQL/ADO    Microsoft-AdoNet-SystemData/BeginExecute|EndExecute
///             · Microsoft.Data.SqlClient.EventSource / System.Data.SqlClient.EventSource
///   TPL/Async  Task Scheduled/Started/Completed · TaskWaitBegin|End · AwaiterContinuation
///   Process    Windows Kernel/Process/Start|Stop · Microsoft-Windows-Kernel-Process/ProcessStart
///   Thread     Windows Kernel/Thread/CSwitch
/// </summary>
public enum TraceEventKind
{
    Unknown = 0,

    // ── CPU / Sampling ──────────────────────────────────────────────────────
    CpuSample,                  // PerfInfo/Sample, SampledProfile, cpu-sampling

    // ── GC ──────────────────────────────────────────────────────────────────
    GCStart,                    // GC/Start
    GCStop,                     // GC/Stop
    GCSuspendEEStart,           // GC/SuspendEEStart  — EE suspended, STW begins
    GCSuspendEEStop,            // GC/SuspendEEStop   — suspension complete
    GCRestartEEStart,           // GC/RestartEEStart  — STW ending
    GCRestartEEStop,            // GC/RestartEEStop   — execution resumed
    GCHeapStats,                // GC/HeapStats
    GCAllocationTick,           // GC/AllocationTick
    GCFinalizeObject,           // GC/FinalizeObject

    // ── Contention / Locks ──────────────────────────────────────────────────
    ContentionStart,            // Contention/Start
    ContentionStop,             // Contention/Stop

    // ── Exceptions ──────────────────────────────────────────────────────────
    ExceptionThrown,            // Exception/Start (first-chance throw)
    ExceptionCatchStart,        // ExceptionCatch/Start
    ExceptionCatchStop,         // ExceptionCatch/Stop

    // ── ThreadPool ──────────────────────────────────────────────────────────
    ThreadPoolEnqueue,          // ThreadPool/Enqueue · FrameworkEventSource/ThreadPoolEnqueueWork
    ThreadPoolDequeue,          // ThreadPool/Dequeue · FrameworkEventSource/ThreadPoolDequeueWork
    ThreadPoolAdjustment,       // ThreadPoolWorkerThreadAdjustment/Adjustment
    WaitHandleWaitStart,        // WaitHandle/Wait/Start
    WaitHandleWaitStop,         // WaitHandle/Wait/Stop

    // ── JIT ─────────────────────────────────────────────────────────────────
    JitMethodStart,             // Method/JittingStarted
    JitMethodLoad,              // Method/LoadVerbose
    JitInliningSucceeded,       // Method/InliningSucceeded
    JitInliningFailed,          // Method/InliningFailedAnsi

    // ── HTTP (ASP.NET Core / Classic ASP.NET / IIS / HttpClient) ─────────────
    HttpRequestStart,           // Microsoft-Windows-ASPNET/Request/Start
                                // AspNetTrace/AspNetReq/Start
                                // Microsoft-AspNetCore-Hosting/Request/Start
    HttpRequestStop,            // …/Stop equivalents
    HttpClientGetRequestStart,  // FrameworkEventSource/GetRequestStream/Start
    HttpClientGetRequestStop,   // FrameworkEventSource/GetRequestStream/Stop
    HttpClientGetResponseStart, // FrameworkEventSource/GetResponse/Start
    HttpClientGetResponseStop,  // FrameworkEventSource/GetResponse/Stop

    // ── TPL / Async ─────────────────────────────────────────────────────────
    TaskScheduled,
    TaskStarted,
    TaskCompleted,
    TaskWaitBegin,
    TaskWaitEnd,
    AwaiterContinuation,

    // ── SQL / ADO.NET ────────────────────────────────────────────────────────
    SqlCommandStart,            // Microsoft-AdoNet-SystemData/BeginExecute
                                // Microsoft.Data.SqlClient.EventSource/SqlCommand/…/Start
    SqlCommandStop,             // Microsoft-AdoNet-SystemData/EndExecute
    SqlConnectionOpen,
    SqlConnectionClose,

    // ── Process / Thread ─────────────────────────────────────────────────────
    ProcessStart,               // Windows Kernel/Process/Start · Microsoft-Windows-Kernel-Process/ProcessStart
    ProcessStop,                // Windows Kernel/Process/Stop
    ThreadContextSwitch,        // Windows Kernel/Thread/CSwitch

    // ── File I/O (ETL only — Microsoft-Windows-Kernel-File) ──────────────────
    FileRead,                   // FileIO/Read
    FileWrite,                  // FileIO/Write
    FileCreate,                 // FileIO/Create
    FileClose,                  // FileIO/Close
    FileFlush,                  // FileIO/Flush

    // ── Sockets — System.Net.Sockets EventSource (.NET 5+) ───────────────────
    SocketConnectStart,
    SocketConnectStop,
    SocketConnectFailed,
    SocketSendStart,
    SocketSendStop,
    SocketReceiveStart,
    SocketReceiveStop,

    // ── DNS — System.Net.NameResolution EventSource ───────────────────────────
    DnsResolutionStart,
    DnsResolutionStop,
    DnsResolutionFailed,

    // ── Kestrel — Microsoft-AspNetCore-Server-Kestrel ─────────────────────────
    KestrelConnectionStart,
    KestrelConnectionStop,
    KestrelConnectionRejected,
    KestrelRequestError,
    KestrelConnectionQueueStart,
    KestrelConnectionQueueStop,

    // ── GC Handles — keyword 0x4000 ──────────────────────────────────────────
    GCHandleCreated,            // GCCreateConcurrentThread / GCHandle/Created
    GCHandleDestroyed,          // GCHandle/Destroyed

    // ── ASP.NET Core pipeline — Microsoft.AspNetCore.* ───────────────────────
    AspNetCoreRouteMatched,
    AspNetCoreAuthStart,
    AspNetCoreAuthStop,
    AspNetCoreAuthFailed,

    // ── OpenTelemetry / DiagnosticSource Activity ─────────────────────────────
    ActivityStart,              // System.Diagnostics.DiagnosticSource/Activity1/Start
    ActivityStop,               // System.Diagnostics.DiagnosticSource/Activity1/Stop
}
