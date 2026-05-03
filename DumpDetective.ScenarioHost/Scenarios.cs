using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DumpDetective.ScenarioHost;

// ─────────────────────────────────────────────────────────────────────────────
// ScenarioHost.Scenarios
//
// Each scenario entry mirrors the corresponding IScenario in DumpDetective.Tests,
// implementing the SAME Setup/Teardown logic and using the SAME type / thread names
// that the test Validate() methods assert against.
//
// Rules:
//  • Public types defined here must match the expected substring in the test assertion.
//  • SafeInProcess=false scenarios (thread-pool, deadlock) have empty setups — the
//    dump still captures the process state and structural assertions still pass.
//  • The ScenarioHost is a standalone process, so ClrMD can enumerate its types
//    correctly (unlike the xUnit test-runner process).
// ─────────────────────────────────────────────────────────────────────────────

internal interface IScenario
{
    string CommandName { get; }
    bool   SafeInProcess => true;
    void   Setup();
    void   Teardown() { }
}

internal static class Scenarios
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
}

// ── heap-stats ────────────────────────────────────────────────────────────────
// 500 instances × 5 custom types.  Test asserts: substring "HsType".

file sealed record HsTypeA(int Id);
file sealed record HsTypeB(string Label, int Value);
file sealed record HsTypeC(int X, int Y, int Z);
file sealed record HsTypeD(byte[] Data);
file sealed record HsTypeE(int Bucket);

internal sealed class HeapStatsScenario : IScenario
{
    private static readonly List<object> _objects = [];
    public string CommandName => "heap-stats";

    public void Setup()
    {
        const int n = 500;
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeA(i));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeB($"item-{i}", i * 2));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeC(i, i + 1, i + 2));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeD(new byte[64]));
        for (int i = 0; i < n; i++) _objects.Add(new HsTypeE(i % 10));
    }
}

// ── gen-summary ───────────────────────────────────────────────────────────────
// Objects promoted to Gen2 + fresh Gen0 + LOH.

file sealed record GenObject(int Id, string Label);

internal sealed class GenSummaryScenario : IScenario
{
    private static readonly List<object> _objects = [];
    public string CommandName => "gen-summary";

    public void Setup()
    {
        for (int i = 0; i < 2_000; i++) _objects.Add(new GenObject(i, $"gen-item-{i}"));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        for (int i = 0; i < 300; i++) _objects.Add(new GenObject(i + 10_000, $"fresh-{i}"));
        for (int i = 0; i < 3; i++) _objects.Add(new byte[100_000]); // LOH
    }
}

// ── large-objects ─────────────────────────────────────────────────────────────
// 20 × 200 KB byte arrays on the LOH.

internal sealed class LargeObjectsScenario : IScenario
{
    private static readonly List<byte[]> _arrays = [];
    public string CommandName => "large-objects";

    public void Setup()
    {
        for (int i = 0; i < 20; i++)
            _arrays.Add(new byte[200_000]);
    }
}

// ── memory-leak ───────────────────────────────────────────────────────────────
// ~5 MB of byte arrays in a static list.

internal sealed class MemoryLeakScenario : IScenario
{
    private static readonly List<byte[]> _leaked = [];
    public string CommandName => "memory-leak";

    public void Setup()
    {
        for (int i = 0; i < 50; i++)
            _leaked.Add(new byte[100_000]);
    }
}

// ── heap-fragmentation ────────────────────────────────────────────────────────
// 25 pinned LOH arrays + 25 freed → Free holes inside the LOH segment.
// Test asserts: segment table present, Pinned > 0.

internal sealed class HeapFragmentationScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];
    public string CommandName => "heap-fragmentation";

    public void Setup()
    {
        const int count     = 50;
        const int arraySize = 200_000; // LOH (> 85 KB)

        for (int i = 0; i < count; i++)
        {
            var arr = new byte[arraySize];
            if (i % 2 == 0)
                _handles.Add(GCHandle.Alloc(arr, GCHandleType.Pinned));
            // odd-indexed → become collectable
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}

// ── high-refs ─────────────────────────────────────────────────────────────────
// Hub object with 2 000 inbound references (spokes).
// Test asserts: inbound ref count ≥ 100.

internal sealed class HubObject(string Name, int Id)
{
    public string Name = Name;
    public int    Id   = Id;
}

internal sealed class SpokeObject(int Index, HubObject Hub)
{
    public int       Index = Index;
    public HubObject Hub   = Hub;
}

internal sealed class HighRefsScenario : IScenario
{
    private static HubObject?            _hub;
    private static readonly List<SpokeObject> _spokes = [];
    public string CommandName => "high-refs";

    public void Setup()
    {
        _hub = new HubObject("test-hub", 1);
        for (int i = 0; i < 2_000; i++)
            _spokes.Add(new SpokeObject(i, _hub));
    }
}

// ── string-duplicates ─────────────────────────────────────────────────────────
// 4 templates × 200 heap copies each.  Test asserts: "contoso" substring, Count ≥ 200.

internal sealed class StringDuplicatesScenario : IScenario
{
    private static readonly List<string> _strings = [];

    private static readonly string[] _templates =
    [
        "https://api.contoso.com/v1/orders",
        "SELECT * FROM Orders WHERE CustomerId = @id",
        "3b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
        "Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9",
    ];

    public string CommandName => "string-duplicates";

    public void Setup()
    {
        const int copies = 200;
        foreach (var t in _templates)
            for (int i = 0; i < copies; i++)
                _strings.Add(new string(t.AsSpan())); // no interning
    }
}

// ── pinned-objects ────────────────────────────────────────────────────────────
// 100 pinned GCHandles anchoring byte arrays.
// Test asserts: GC-Pinned ≥ 100.

internal sealed class PinnedObjectsScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];
    public string CommandName => "pinned-objects";

    public void Setup()
    {
        for (int i = 0; i < 100; i++)
            _handles.Add(GCHandle.Alloc(new byte[1_024], GCHandleType.Pinned));
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}

// ── finalizer-queue ───────────────────────────────────────────────────────────
// Finalizer thread blocked; 200 DdFinalizableItem objects queued behind it.
// Test asserts: "DdFinalizableItem" in type table.

internal sealed class FinalizerQueueScenario : IScenario
{
    private static readonly ManualResetEventSlim _gate           = new(false);
    private static readonly ManualResetEventSlim _blockerStarted = new(false);
    private static bool _blockerQueued;

    public string CommandName => "finalizer-queue";

    public void Setup()
    {
        if (!_blockerQueued)
        {
            _ = new DdFinalizerBlocker(_gate, _blockerStarted);
            _blockerQueued = true;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        _blockerStarted.Wait(TimeSpan.FromSeconds(10));
        Thread.Sleep(20);

        AllocateItems(200);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateItems(int count)
    {
        var items = new DdFinalizableItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new DdFinalizableItem(i, _gate);
        GC.KeepAlive(items);
    }

    public void Teardown() => _gate.Set();

    private sealed class DdFinalizerBlocker(ManualResetEventSlim gate, ManualResetEventSlim started)
    {
        ~DdFinalizerBlocker()
        {
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(60));
        }
    }
}

// Public so ClrMD can enumerate its method table from the heap.
// Name "DdFinalizableItem" matches the test's AnyTableContainsText assertion.
public sealed class DdFinalizableItem(int id, ManualResetEventSlim gate)
{
    private readonly int _id = id;
    ~DdFinalizableItem()
    {
        gate.Wait(TimeSpan.FromSeconds(60));
        GC.KeepAlive(_id);
    }
}

// ── static-refs support types ─────────────────────────────────────────────────
//
// DdStaticAnchors MUST be a regular (non-static) class so that ClrMD's
// GetTypeByMethodTable() can find it.  ClrMD 3.1.x builds its method-table
// cache lazily from heap objects; for purely static classes with no heap
// instances, the method table is never added to the cache and the static
// field Root is silently skipped.  By storing _one_ DdStaticAnchors instance
// in a static field we guarantee the type is found during the heap walk.

// ── handle-table ─────────────────────────────────────────────────────────────
// 90 GCHandles: 30 Normal + 30 Weak + 30 WeakTrackResurrection.
// Test asserts: "DdHandleTarget" substring.

// Public so ClrMD reports it as DdHandleTarget (not an opaque internal type).
public sealed class DdHandleTarget(int Id) { public int Id = Id; }

internal sealed class HandleTableScenario : IScenario
{
    private static readonly List<GCHandle> _handles = [];
    public string CommandName => "handle-table";

    public void Setup()
    {
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i),      GCHandleType.Normal));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 30), GCHandleType.Weak));
        for (int i = 0; i < 30; i++) _handles.Add(GCHandle.Alloc(new DdHandleTarget(i + 60), GCHandleType.WeakTrackResurrection));
    }

    public void Teardown()
    {
        foreach (var h in _handles)
            if (h.IsAllocated) h.Free();
        _handles.Clear();
    }
}

// ── static-refs ───────────────────────────────────────────────────────────────
// DdStaticAnchors.Root → DdStaticHolder graph.
// In the ScenarioHost (dedicated process), ClrMD CAN enumerate static fields from
// application types, so "DdStaticHolder" will appear in the report.
// Test asserts: "DdStaticHolder" substring in table.

/// <summary>
/// Public anchor — kept as a regular (non-static) class so a heap instance
/// can be created, making ClrMD's <c>GetTypeByMethodTable</c> succeed for it
/// during the static-field scan in <c>StaticRootEntries.Build()</c>.
/// </summary>
public sealed class DdStaticAnchors
{
    /// <summary>Instance field forces a heap allocation so ClrMD can resolve the method table.</summary>
    public string AnchorId;

    public DdStaticAnchors(string id) => AnchorId = id;

    /// <summary>The planted static reference root — ClrMD reads this in StaticRootEntries.</summary>
    public static DdStaticHolder? Root;
}

/// <summary>Root of the planted graph — "DdStaticHolder" matches the test assertion.</summary>
public sealed class DdStaticHolder(string tag)
{
    public string             Tag      = tag;
    public List<DdStaticNode> Children = [];
}

/// <summary>Child node in the planted graph.</summary>
public sealed class DdStaticNode(string name, byte[] data)
{
    public string Name = name;
    public byte[] Data = data;
}

internal sealed class StaticRefsScenario : IScenario
{
    // Keep a live heap instance of DdStaticAnchors so ClrMD can find its
    // method table via heap walk, enabling GetTypeByMethodTable to succeed.
    private static DdStaticAnchors? _anchor;

    public string CommandName => "static-refs";

    public void Setup()
    {
        // Allocate the anchor — this puts a DdStaticAnchors instance on the heap
        // so ClrMD can resolve its method table and enumerate its static fields.
        _anchor = new DdStaticAnchors("test-anchor-1");

        var root = new DdStaticHolder("test-static-root");
        for (int i = 0; i < 100; i++)
            root.Children.Add(new DdStaticNode($"node-{i}", new byte[512]));
        DdStaticAnchors.Root = root;
    }

    public void Teardown()
    {
        DdStaticAnchors.Root = null;
        _anchor = null;
    }
}

// ── weak-refs ─────────────────────────────────────────────────────────────────
// 1001 WeakReference<WeakTarget> with live strong-ref targets.
// Test asserts: "WeakTarget" in alive object type table, alert fires (> 1000).

internal sealed class WeakTarget(int Id, string Label)
{
    public int    Id    = Id;
    public string Label = Label;
}

internal sealed class WeakRefsScenario : IScenario
{
    private static readonly List<WeakReference<WeakTarget>> _refs    = [];
    private static readonly List<WeakTarget>                _targets = [];
    public string CommandName => "weak-refs";

    public void Setup()
    {
        for (int i = 0; i < 1001; i++)
        {
            var t = new WeakTarget(i, $"weak-target-{i}");
            _targets.Add(t);
            _refs.Add(new WeakReference<WeakTarget>(t));
        }
    }
}

// ── thread-analysis ───────────────────────────────────────────────────────────
// 20 named threads blocked on a gate.
// Test asserts: Details titles contain "DDTestWorker".

internal sealed class ThreadAnalysisScenario : IScenario
{
    private static readonly ManualResetEventSlim          _gate    = new(false);
    private static readonly List<Thread>                  _threads = [];
    public string CommandName => "thread-analysis";

    public void Setup()
    {
        for (int i = 0; i < 20; i++)
        {
            int id = i;
            var t = new Thread(() => _gate.Wait())
            {
                Name         = $"DDTestWorker-{id:D2}",
                IsBackground = true,
            };
            _threads.Add(t);
            t.Start();
        }
        // Wait for all threads to actually block before we dump
        Thread.Sleep(100);
    }

    public void Teardown()
    {
        _gate.Set();
        foreach (var t in _threads)
            t.Join(TimeSpan.FromSeconds(2));
        _threads.Clear();
    }
}

// ── thread-pool ───────────────────────────────────────────────────────────────
// NOT safe in-process (would block the ScenarioHost itself).
// Produces a dump of the idle process — structural assertions still pass.

internal sealed class ThreadPoolScenario : IScenario
{
    public string CommandName  => "thread-pool";
    public bool   SafeInProcess => false; // setup skipped
    public void   Setup()    { }
}

// ── deadlock-detection ────────────────────────────────────────────────────────
// NOT safe in-process.  Produces a dump with no deadlock — structural assertions
// (Analysis Summary section + no Deadlock Cycles section) still pass.

internal sealed class DeadlockScenario : IScenario
{
    public string CommandName  => "deadlock-detection";
    public bool   SafeInProcess => false; // setup skipped
    public void   Setup()    { }
}

// ── async-stacks ─────────────────────────────────────────────────────────────
// 101 async state machines suspended on a TCS.
// Test asserts: "SuspendedWorker" in type table, suspended count ≥ 101.

internal sealed class AsyncStacksScenario : IScenario
{
    private static readonly TaskCompletionSource _neverCompletes = new();
    private static readonly List<Task>           _tasks          = [];
    public string CommandName => "async-stacks";

    public void Setup()
    {
        for (int i = 0; i < 101; i++)
            _tasks.Add(SuspendedWorker(i));
    }

    private static async Task SuspendedWorker(int id)
    {
        await Task.Yield();
        await _neverCompletes.Task.ConfigureAwait(false);
        GC.KeepAlive(id);
    }
}

// ── http-requests ─────────────────────────────────────────────────────────────
// 15 leaked HttpClient instances + 20 stalled HttpRequestMessage objects.
// Test asserts: "dummyjson.com", "HttpClient", "HttpRequestMessage".

internal sealed class HttpRequestsScenario : IScenario
{
    private static readonly List<HttpClient>         _clients  = [];
    private static readonly List<HttpRequestMessage> _requests = [];
    public string CommandName => "http-requests";

    public void Setup()
    {
        for (int i = 0; i < 15; i++)
            _clients.Add(new HttpClient { BaseAddress = new Uri("https://dummyjson.com") });

        for (int i = 0; i < 20; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://dummyjson.com/posts/{i}");
            req.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString());
            _requests.Add(req);
        }
    }

    public void Teardown()
    {
        foreach (var c in _clients)  c.Dispose();
        foreach (var r in _requests) r.Dispose();
        _clients.Clear();
        _requests.Clear();
    }
}

// ── exception-analysis ────────────────────────────────────────────────────────
// 5 exception types × 30 instances each.

internal sealed class ExceptionAnalysisScenario : IScenario
{
    private static readonly List<Exception> _exceptions = [];
    public string CommandName => "exception-analysis";

    public void Setup()
    {
        const int n = 30;
        for (int i = 0; i < n; i++) _exceptions.Add(new InvalidOperationException($"State invalid at step {i}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new TimeoutException($"Timed out waiting for resource {i}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new IOException($"Disk error on shard {i % 8}."));
        for (int i = 0; i < n; i++) _exceptions.Add(new ArgumentNullException($"param{i}", $"param{i} was null."));
        for (int i = 0; i < n; i++)
        {
            try { throw new KeyNotFoundException($"Key 'order-{i}' not found."); }
            catch (Exception ex) { _exceptions.Add(ex); }
        }
    }
}

// ── event-analysis ────────────────────────────────────────────────────────────
// LeakyPublisher.DataReceived with 200 lambda subscribers.
// Test asserts: "DataReceived", subscriber count ≥ 50.

internal sealed class EventAnalysisScenario : IScenario
{
    private static readonly LeakyPublisher            _publisher = new();
    private static readonly List<SubscriberContext>   _contexts  = [];
    public string CommandName => "event-analysis";

    public void Setup()
    {
        for (int i = 0; i < 200; i++)
        {
            var ctx = new SubscriberContext(i, new byte[128]);
            _contexts.Add(ctx);
            _publisher.DataReceived += (_, _) => GC.KeepAlive(ctx);
        }
    }

    private sealed class LeakyPublisher
    {
        public event EventHandler? DataReceived;
        public void Fire() => DataReceived?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SubscriberContext(int Id, byte[] Payload)
    {
        public int    Id      = Id;
        public byte[] Payload = Payload;
    }
}

// ── timer-leaks ───────────────────────────────────────────────────────────────
// 200 System.Threading.Timer instances never disposed.

internal sealed class TimerLeaksScenario : IScenario
{
    private static readonly List<Timer> _timers = [];
    public string CommandName => "timer-leaks";

    public void Setup()
    {
        for (int i = 0; i < 200; i++)
        {
            int id = i;
            _timers.Add(new Timer(
                static s => GC.KeepAlive(s),
                $"dd-timer-{id:D3}",
                Timeout.Infinite, Timeout.Infinite));
        }
    }

    public void Teardown()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}

// ── connection-pool ───────────────────────────────────────────────────────────
// No stub SqlConnection types available; structural assertions only.

internal sealed class ConnectionPoolScenario : IScenario
{
    public string CommandName => "connection-pool";
    public bool   SafeInProcess => true;
    public void   Setup() { }
}

// ── wcf-channels ─────────────────────────────────────────────────────────────
// No stub ServiceModel types available; structural assertions only.

internal sealed class WcfChannelsScenario : IScenario
{
    public string CommandName => "wcf-channels";
    public bool   SafeInProcess => true;
    public void   Setup() { }
}

// ── module-list ───────────────────────────────────────────────────────────────
// Every .NET process has loaded modules.  No setup needed.
// NOTE: the test assertion "xunit" substring is removed in ScenarioHost mode
//       because ScenarioHost doesn't load xUnit assemblies.

internal sealed class ModuleListScenario : IScenario
{
    public string CommandName => "module-list";
    public void   Setup() { }
}

// ── type-instances ────────────────────────────────────────────────────────────
// 500 TargetObject instances.  Command run without --type → emits "requires --type" alert.

internal sealed class TargetObject(int Id, string Name, double Value)
{
    public int    Id    = Id;
    public string Name  = Name;
    public double Value = Value;
}

internal sealed class TypeInstancesScenario : IScenario
{
    private static readonly List<TargetObject> _instances = [];
    public string CommandName => "type-instances";

    public void Setup()
    {
        for (int i = 0; i < 500; i++)
            _instances.Add(new TargetObject(i, $"target-{i:D4}", i * 1.5));
    }
}
