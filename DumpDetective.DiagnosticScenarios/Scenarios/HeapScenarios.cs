namespace DumpDetective.DiagnosticScenarios.Scenarios;

// Scenarios for: heap-stats, gen-summary, large-objects, memory-leak
internal static class HeapScenarios
{
    // ── heap-stats ────────────────────────────────────────────────────────────
    // 10 custom types × 500 instances each → shows diverse type distribution
    private static readonly List<object> _heapStatObjects = [];

    public static IResult TriggerHeapStats()
    {
        const int perType = 500;
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeA(i));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeB($"item-{i}", i * 2));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeC(i, i + 1, i + 2));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeD(new byte[64]));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeE(i % 10));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeF([i, i + 1, i + 2]));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeG(i.ToString(), i));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeH(i * 3.14));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeI(i, i.ToString()));
        for (int i = 0; i < perType; i++) _heapStatObjects.Add(new HsTypeJ(Guid.NewGuid()));
        return Results.Ok(new { message = $"Created {_heapStatObjects.Count} objects across 10 types.", command = "DumpDetective heap-stats <dump.dmp>" });
    }

    public static string Status => $"heap-stats: {_heapStatObjects.Count} objects";

    // ── gen-summary ───────────────────────────────────────────────────────────
    // Allocate objects across generations: some ephemeral (Gen0), some promoted (Gen2),
    // some on LOH. Taking a dump after GC.Collect shows a realistic gen distribution.
    private static readonly List<object> _genObjects = [];

    public static IResult TriggerGenSummary()
    {
        // Allocate 2 000 small objects that will be promoted to Gen1/Gen2
        for (int i = 0; i < 2_000; i++)
            _genObjects.Add(new GenObject(i, $"gen-item-{i}"));

        // Force two full collections so existing objects reach Gen2
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

        // Allocate 500 more that remain in Gen0/Gen1 at dump time
        for (int i = 0; i < 500; i++)
            _genObjects.Add(new GenObject(i + 10_000, $"fresh-{i}"));

        // Allocate a few LOH items (>85 KB each)
        for (int i = 0; i < 5; i++)
            _genObjects.Add(new byte[100_000]);

        return Results.Ok(new { message = $"Gen2-promoted objects: ~2 000, fresh Gen0: ~500, LOH arrays: 5", command = "DumpDetective gen-summary <dump.dmp>" });
    }

    public static string GenSummaryStatus => $"gen-summary: {_genObjects.Count} objects";

    // ── large-objects ─────────────────────────────────────────────────────────
    // 50 × 200 KB byte arrays → all land in LOH (threshold is 85 000 bytes)
    private static readonly List<byte[]> _lohArrays = [];

    public static IResult TriggerLargeObjects()
    {
        for (int i = 0; i < 50; i++)
            _lohArrays.Add(new byte[200_000]); // 200 KB each → LOH
        return Results.Ok(new { message = $"{_lohArrays.Count} large objects on LOH ({_lohArrays.Count * 200} KB total).", command = "DumpDetective large-objects <dump.dmp>" });
    }

    public static string LargeObjectsStatus => $"large-objects: {_lohArrays.Count} LOH arrays";

    // ── memory-leak ───────────────────────────────────────────────────────────
    // Each call appends ~1 MB to a static list that is never cleared.
    private static readonly List<byte[]> _leakedMemory = [];

    public static IResult TriggerMemoryLeak()
    {
        for (int i = 0; i < 10; i++)
            _leakedMemory.Add(new byte[100_000]); // 10 × 100 KB = ~1 MB per call
        long totalBytes = _leakedMemory.LongCount() * 100_000;
        return Results.Ok(new { message = $"Leaked ~{totalBytes / 1_048_576} MB total across {_leakedMemory.Count} arrays.", command = "DumpDetective memory-leak <dump.dmp>", hint = "Call this endpoint multiple times to grow the leak." });
    }

    public static string MemoryLeakStatus => $"memory-leak: {_leakedMemory.Count * 100_000 / 1_048_576} MB leaked";

    public static void Reset()
    {
        _heapStatObjects.Clear();
        _genObjects.Clear();
        _lohArrays.Clear();
        _leakedMemory.Clear();
    }

    // ── Custom types for heap-stats ───────────────────────────────────────────
    private sealed class HsTypeA(int value) { public int Value = value; }
    private sealed class HsTypeB(string name, int count) { public string Name = name; public int Count = count; }
    private sealed class HsTypeC(int x, int y, int z) { public int X = x, Y = y, Z = z; }
    private sealed class HsTypeD(byte[] data) { public byte[] Data = data; }
    private sealed class HsTypeE(int level) { public int Level = level; public string Tag = $"tag-{level}"; }
    private sealed class HsTypeF(int[] items) { public int[] Items = items; }
    private sealed class HsTypeG(string key, int id) { public string Key = key; public int Id = id; }
    private sealed class HsTypeH(double value) { public double Value = value; }
    private sealed class HsTypeI(int seq, string label) { public int Seq = seq; public string Label = label; }
    private sealed class HsTypeJ(Guid id) { public Guid Id = id; }

    private sealed class GenObject(int id, string name) { public int Id = id; public string Name = name; }
}
