using System.Runtime.InteropServices;

namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: high-refs, heap-fragmentation, pinned-objects, gc-roots,
//                finalizer-queue, handle-table, static-refs, weak-refs
internal static class GcScenarios
{
    // ── high-refs ─────────────────────────────────────────────────────────────
    // One "hub" object referenced by 5 000 spoke objects stored in a static list.
    // DumpDetective high-refs walks all objects' reference fields and counts inbound edges.
    private static HubObject? _hub;
    private static readonly List<SpokeObject> _spokes = [];

    public static IResult TriggerHighRefs()
    {
        _hub = new HubObject("central-hub", 42);
        _spokes.Clear();
        for (int i = 0; i < 5_000; i++)
            _spokes.Add(new SpokeObject(i, _hub));
        return Results.Ok(new { message = $"Hub object with {_spokes.Count} inbound references created.", command = "DumpDetective high-refs <dump.dmp>" });
    }

    public static string HighRefsStatus => $"high-refs: hub={(_hub is null ? "none" : "active")}, spokes={_spokes.Count}";

    // ── heap-fragmentation ────────────────────────────────────────────────────
    // Allocate 400 × 20 KB arrays. Pin every other one, force GC to collect the
    // unpinned ones. The pinned arrays cannot move, leaving gaps in the SOH.
    private static readonly List<GCHandle> _fragmentationHandles = [];

    public static IResult TriggerHeapFragmentation()
    {
        const int count = 400;
        const int arraySize = 20_000; // 20 KB — stays on SOH (< 85 KB)
        var temporary = new List<byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            var arr = new byte[arraySize];
            if (i % 2 == 0)
                _fragmentationHandles.Add(GCHandle.Alloc(arr, GCHandleType.Pinned));
            else
                temporary.Add(arr); // will be eligible for collection
        }

        // Collect the unpinned arrays, leaving holes around the pinned ones
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.KeepAlive(temporary); // suppress optimiser from collecting before GC call
        temporary.Clear();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

        return Results.Ok(new { message = $"{_fragmentationHandles.Count} pinned arrays creating SOH fragmentation.", command = "DumpDetective heap-fragmentation <dump.dmp>" });
    }

    public static string FragmentationStatus => $"heap-fragmentation: {_fragmentationHandles.Count} pinned handles";

    // ── pinned-objects ────────────────────────────────────────────────────────
    // 200 GCHandle.Pinned handles — each anchors a byte[] so the GC cannot compact.
    private static readonly List<GCHandle> _pinnedHandles = [];

    public static IResult TriggerPinnedObjects()
    {
        for (int i = 0; i < 200; i++)
            _pinnedHandles.Add(GCHandle.Alloc(new byte[1_024], GCHandleType.Pinned));
        return Results.Ok(new { message = $"{_pinnedHandles.Count} pinned GCHandles active.", command = "DumpDetective pinned-objects <dump.dmp>" });
    }

    public static string PinnedStatus => $"pinned-objects: {_pinnedHandles.Count} handles";

    // ── gc-roots ──────────────────────────────────────────────────────────────
    // Objects anchored via three root kinds: static field, Normal GCHandle, and
    // a WeakReference (tracks resurrection). All rooted objects are unreachable
    // by application code except through these roots.
    private static RootObject? _staticRoot;
    private static GCHandle _normalHandle;
    private static readonly List<GCHandle> _rootHandles = [];

    public static IResult TriggerGcRoots()
    {
        _staticRoot = new RootObject("static-root", new byte[4_096]);
        _normalHandle = GCHandle.Alloc(new RootObject("handle-root", new byte[4_096]), GCHandleType.Normal);
        for (int i = 0; i < 50; i++)
            _rootHandles.Add(GCHandle.Alloc(new RootObject($"root-{i}", new byte[512]), GCHandleType.Normal));
        return Results.Ok(new { message = $"1 static root + {1 + _rootHandles.Count} GCHandle roots created.", command = "DumpDetective gc-roots <dump.dmp>" });
    }

    public static string RootsStatus => $"gc-roots: static={(_staticRoot is null ? "none" : "active")}, handles={_rootHandles.Count + (_normalHandle.IsAllocated ? 1 : 0)}";

    // ── finalizer-queue ───────────────────────────────────────────────────────
    // 500 objects with finalizers are created. One FinalizerBlocker object's
    // finalizer waits on a ManualResetEventSlim, blocking the finalizer thread
    // so the rest of the queue backs up.
    private static readonly List<FinalizableItem> _finalizables = [];
    private static readonly ManualResetEventSlim _finalizerGate = new(false);
    private static bool _finalizerBlocked;

    public static IResult TriggerFinalizerQueue()
    {
        if (!_finalizerBlocked)
        {
            // The blocker's finalizer will wait on the gate — stalls the finalizer thread
            GC.ReRegisterForFinalize(new FinalizerBlocker(_finalizerGate));
            _finalizerBlocked = true;
        }

        for (int i = 0; i < 500; i++)
            _finalizables.Add(new FinalizableItem(i));

        // Make all 500 items unreachable so they enter the finalizer queue
        var snapshot = _finalizables.ToList();
        _finalizables.Clear();
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        GC.KeepAlive(snapshot);

        return Results.Ok(new { message = "500 finalizable objects queued; finalizer thread is blocked.", command = "DumpDetective finalizer-queue <dump.dmp>", hint = "POST /api/diagscenario/reset unblocks the finalizer thread." });
    }

    public static string FinalizerStatus => $"finalizer-queue: blocked={_finalizerBlocked}";

    // ── handle-table ──────────────────────────────────────────────────────────
    // 300 GCHandles of three types: Normal, Weak, WeakTrackResurrection
    private static readonly List<GCHandle> _mixedHandles = [];

    public static IResult TriggerHandleTable()
    {
        for (int i = 0; i < 100; i++) _mixedHandles.Add(GCHandle.Alloc(new HandlePayload(i, "normal"), GCHandleType.Normal));
        for (int i = 0; i < 100; i++) _mixedHandles.Add(GCHandle.Alloc(new HandlePayload(i, "weak"), GCHandleType.Weak));
        for (int i = 0; i < 100; i++) _mixedHandles.Add(GCHandle.Alloc(new HandlePayload(i, "weak-track"), GCHandleType.WeakTrackResurrection));
        return Results.Ok(new { message = $"{_mixedHandles.Count} GCHandles (100 Normal, 100 Weak, 100 WeakTrackResurrection).", command = "DumpDetective handle-table <dump.dmp>" });
    }

    public static string HandleStatus => $"handle-table: {_mixedHandles.Count} handles";

    // ── static-refs ───────────────────────────────────────────────────────────
    // A large object graph anchored to static fields.
    private static StaticGraphRoot? _graphRoot;

    public static IResult TriggerStaticRefs()
    {
        var root = new StaticGraphRoot("root");
        for (int i = 0; i < 200; i++)
        {
            var child = new StaticGraphNode($"child-{i}", new byte[2_048]);
            for (int j = 0; j < 5; j++)
                child.Children.Add(new StaticGraphLeaf($"leaf-{i}-{j}"));
            root.Children.Add(child);
        }
        _graphRoot = root;
        int total = 1 + root.Children.Count + root.Children.Sum(c => c.Children.Count);
        return Results.Ok(new { message = $"Static object graph: 1 root + {root.Children.Count} nodes + {root.Children.Sum(c => c.Children.Count)} leaves = {total} objects.", command = "DumpDetective static-refs <dump.dmp>" });
    }

    public static string StaticStatus => $"static-refs: graph={(_graphRoot is null ? "none" : $"{1 + _graphRoot.Children.Count + _graphRoot.Children.Sum(c => c.Children.Count)} objects")}";

    // ── weak-refs ─────────────────────────────────────────────────────────────
    // 1 000 WeakReference<T> objects — DumpDetective weak-refs enumerates them.
    private static readonly List<WeakReference<WeakTarget>> _weakRefs = [];

    public static IResult TriggerWeakRefs()
    {
        for (int i = 0; i < 1_000; i++)
            _weakRefs.Add(new WeakReference<WeakTarget>(new WeakTarget(i, $"weak-{i}")));
        return Results.Ok(new { message = $"{_weakRefs.Count} WeakReference<T> objects on heap.", command = "DumpDetective weak-refs <dump.dmp>" });
    }

    public static string WeakStatus => $"weak-refs: {_weakRefs.Count} WeakReference<T> objects";

    public static void Reset()
    {
        _hub = null;
        _spokes.Clear();

        foreach (var h in _fragmentationHandles) if (h.IsAllocated) h.Free();
        _fragmentationHandles.Clear();

        foreach (var h in _pinnedHandles) if (h.IsAllocated) h.Free();
        _pinnedHandles.Clear();

        _staticRoot = null;
        if (_normalHandle.IsAllocated) _normalHandle.Free();
        foreach (var h in _rootHandles) if (h.IsAllocated) h.Free();
        _rootHandles.Clear();

        _finalizerGate.Set(); // unblock finalizer thread
        _finalizerBlocked = false;
        _finalizables.Clear();

        foreach (var h in _mixedHandles) if (h.IsAllocated) h.Free();
        _mixedHandles.Clear();

        _graphRoot = null;
        _weakRefs.Clear();
    }

    // ── Supporting types ──────────────────────────────────────────────────────
    private sealed class HubObject(string name, int id) { public string Name = name; public int Id = id; }
    private sealed class SpokeObject(int id, HubObject hub) { public int Id = id; public HubObject Hub = hub; }

    private sealed class RootObject(string name, byte[] data) { public string Name = name; public byte[] Data = data; }

    private sealed class FinalizableItem(int id) { public int Id = id; ~FinalizableItem() { } }

    private sealed class FinalizerBlocker(ManualResetEventSlim gate)
    {
        private readonly ManualResetEventSlim _gate = gate;
        ~FinalizerBlocker()
        {
            // Blocks the finalizer thread until Reset() signals the gate
            _gate.Wait();
        }
    }

    private sealed class HandlePayload(int id, string kind) { public int Id = id; public string Kind = kind; }

    private sealed class StaticGraphRoot(string name) { public string Name = name; public List<StaticGraphNode> Children = []; }
    private sealed class StaticGraphNode(string name, byte[] data) { public string Name = name; public byte[] Data = data; public List<StaticGraphLeaf> Children = []; }
    private sealed class StaticGraphLeaf(string label) { public string Label = label; }

    private sealed class WeakTarget(int id, string name) { public int Id = id; public string Name = name; }
}
