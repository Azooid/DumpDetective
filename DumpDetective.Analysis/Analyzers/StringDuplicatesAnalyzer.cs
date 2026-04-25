using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Finds duplicate string values on the managed heap, reporting wasted memory
/// from interning opportunities.
/// Fast path: reads the pre-built <see cref="HeapSnapshot.StringGroups"/> dictionary
/// (built by <see cref="Consumers.StringGroupConsumer"/> during the main heap walk)
/// and releases it immediately after reading to free ~1–2 GB.
/// Slow path: performs its own heap walk, grouping strings by value using
/// <c>ClrObject.AsString(maxLength: 512)</c>.
/// </summary>
public sealed class StringDuplicatesAnalyzer
{
    public StringDuplicatesData Analyze(DumpContext ctx)
    {
        // Fast path — reuse HeapSnapshot when available
        if (ctx.Snapshot is { } snap)
        {
            var groups = new List<StringDupGroup>(snap.StringGroups.Count);
            foreach (var kv in snap.StringGroups)
                groups.Add(new StringDupGroup(kv.Key, kv.Value.Count, kv.Value.TotalSize));
            var strResult = new StringDuplicatesData(groups, snap.TotalStringCount, snap.TotalStringSize);
            // StringGroups is only read here — release it now to free memory.
            snap.ReleaseStringGroups();
            return strResult;
        }

        // Slow path — own heap walk
        var dict  = new Dictionary<string, (int Count, long TotalSize)>(StringComparer.Ordinal);
        long totalStrings = 0, totalSize = 0;

        CommandBase.RunStatus("Scanning strings...", update =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type?.Name != "System.String") continue;
                totalStrings++;
                if ((totalStrings & 0x3FF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning strings \u2014 {totalStrings:N0} strings  \u2022  {dict.Count:N0} unique...");
                    sw.Restart();
                }
                long size  = (long)obj.Size;
                totalSize += size;
                var val    = obj.AsString(maxLength: 512) ?? string.Empty;
                if (dict.TryGetValue(val, out var e)) dict[val] = (e.Count + 1, e.TotalSize + size);
                else                                  dict[val] = (1, size);
            }
        });

        var result = new List<StringDupGroup>(dict.Count);
        foreach (var kv in dict) result.Add(new StringDupGroup(kv.Key, kv.Value.Count, kv.Value.TotalSize));
        return new StringDuplicatesData(result, totalStrings, totalSize);
    }
}
